using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;

namespace ChatServer
{
    [ApiController, Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly string _jwtSecret;

        public AuthController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
            _jwtSecret = _config["Jwt:Secret"] ?? throw new Exception("JWT Secret не найден в appsettings.json");
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            if (req.Username.Length < 3 || req.Password.Length < 4)
                return BadRequest(new ServerResponse { Success = false, Message = "Короткий логин/пароль" });

            if (await _db.Users.AnyAsync(u => u.Username == req.Username))
                return Conflict(new ServerResponse { Success = false, Message = "Имя занято" });

            var user = new User
            {
                Username = req.Username,
                Nickname = string.IsNullOrWhiteSpace(req.Nickname) ? req.Username : req.Nickname,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                IsOnline = false,
                LastSeen = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok(new ServerResponse
            {
                Success = true,
                UserId = user.Id,
                Username = user.Username,
                Nickname = user.Nickname,
                Message = GenerateJwtToken(user)
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
            if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Unauthorized(new ServerResponse { Success = false, Message = "Неверный логин или пароль" });

            user.IsOnline = true;
            user.LastSeen = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new ServerResponse
            {
                Success = true,
                UserId = user.Id,
                Username = user.Username,
                Nickname = user.Nickname,
                Message = GenerateJwtToken(user)
            });
        }

        private string GenerateJwtToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var token = new JwtSecurityToken(
                claims: new[] {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim("nickname", user.Nickname)
                },
                expires: DateTime.Now.AddDays(int.Parse(_config["Jwt:ExpireDays"] ?? "30")),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}