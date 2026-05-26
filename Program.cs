using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 100 * 1024 * 1024;
    options.EnableDetailedErrors = true;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024;
});
builder.Services.AddDbContext<AppDbContext>();

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(origin => true));
});

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]))
        };

        o.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/chathub"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(origin => true));
});

builder.WebHost.UseKestrel(options =>
{
    options.ListenLocalhost(5001, listenOptions =>
    {
        listenOptions.UseHttps();
    });
});
var app = builder.Build();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.Run("https://localhost:5001");

// ХАБ SIGNALR
[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private static Dictionary<string, int> _connections = new();
    private static Dictionary<int, string> _userConnections = new();
    private static Dictionary<int, List<int>> _pendingMessages = new();

    public ChatHub(AppDbContext db)
    {
        _db = db;
    }

    public async Task GetRoomFiles(string roomId)
    {
        var files = await _db.FileAttachments
            .Where(f => f.RoomId == roomId)
            .OrderBy(f => f.SentAt)
            .ToListAsync();

        foreach (var file in files)
        {
            var sender = await _db.Users.FindAsync(file.SenderId);
            if (sender != null)
            {
                await Clients.Caller.SendAsync("ReceiveFileInfo",
                    roomId,
                    sender.Username,
                    file.FileName,
                    file.ContentType,
                    file.FileSize,
                    file.Id,
                    file.SentAt);
            }
        }
    }

    public async Task SetAvatar(string userName, byte[] encryptedAvatarData)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null) return;

        user.AvatarData = encryptedAvatarData;
        await _db.SaveChangesAsync();

        await Clients.All.SendAsync("AvatarUpdated", user.Username, encryptedAvatarData);
    }

    public async Task UpdateNickname(string newNickname)
    {
        if (!_connections.TryGetValue(Context.ConnectionId, out int userId)) return;

        var user = await _db.Users.FindAsync(userId);
        if (user == null) return;

        user.Nickname = newNickname;
        await _db.SaveChangesAsync();

        await Clients.All.SendAsync("NicknameUpdated", user.Username, newNickname);
    }

    public async Task SetPublicKey(string userName, string publicKey)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null) return;

        user.RSAPublicKey = publicKey;
        await _db.SaveChangesAsync();

        Console.WriteLine($"SetPublicKey: {userName}, ключ сохранён, длина={user.RSAPublicKey?.Length ?? 0}");
    }

    public async Task GetPublicKey(string userName)
    {
        Console.WriteLine($"=== GetPublicKey: {userName} ===");
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        Console.WriteLine($"user найден: {user != null}");
        Console.WriteLine($"RSAPublicKey is null: {user?.RSAPublicKey == null}");
        if (user == null) return;
        await Clients.Caller.SendAsync("PublicKeyReceived", user.Username, user.RSAPublicKey);
    }

    public async Task SendEncryptedKey(string targetUserName, string roomId, byte[] encryptedKey)
    {
        Console.WriteLine($"=== SendEncryptedKey ===");
        Console.WriteLine($"targetUserName: {targetUserName}");
        Console.WriteLine($"roomId: {roomId}");
        Console.WriteLine($"encryptedKey size: {encryptedKey.Length}");

        var targetUser = _db.Users.FirstOrDefault(u => u.Username == targetUserName);
        Console.WriteLine($"targetUser найден: {targetUser != null}");

        if (targetUser == null) return;

        Console.WriteLine($"_userConnections содержит targetUser.Id ({targetUser.Id}): {_userConnections.ContainsKey(targetUser.Id)}");

        if (_userConnections.TryGetValue(targetUser.Id, out string connectionId))
        {
            await Clients.Client(connectionId).SendAsync("RoomKeyReceived", roomId, encryptedKey);
            Console.WriteLine($"RoomKeyReceived отправлен клиенту {targetUserName}");
        }
        else
        {
            _db.PendingKeys.Add(new PendingKey
            {
                TargetUserId = targetUser.Id,
                RoomId = roomId,
                EncryptedKey = encryptedKey
            });
            await _db.SaveChangesAsync();
            Console.WriteLine($"Ключ сохранён в PendingKeys для userId={targetUser.Id}, roomId={roomId}");
        }
    }

    public async Task GetAvatar(string userName)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null || user.AvatarData == null) return;
        await Clients.Caller.SendAsync("AvatarReceived", user.Username, user.AvatarData);
    }

    public async Task<int> StartFileUpload(string roomId, string userName, string fileName, string contentType, long fileSize, int totalChunks)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null) return -1;

        var attachment = new FileAttachment
        {
            FileName = fileName,
            ContentType = contentType,
            FileSize = fileSize,
            FilePath = "",
            RoomId = roomId,
            SenderId = user.Id,
            SentAt = DateTime.UtcNow,
            TotalChunks = totalChunks
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        return attachment.Id;
    }

    public async Task UploadFileChunk(int fileId, int chunkIndex, int totalChunks, byte[] encryptedChunk)
    {
        string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", fileId.ToString());
        if (!Directory.Exists(tempDir))
            Directory.CreateDirectory(tempDir);

        string chunkPath = Path.Combine(tempDir, $"{chunkIndex}.chunk");
        await File.WriteAllBytesAsync(chunkPath, encryptedChunk);
        if (chunkIndex == totalChunks - 1)
        {
            await AssembleFile(fileId, totalChunks);
        }
    }

    private async Task AssembleFile(int fileId, int totalChunks)
    {
        var attachment = await _db.FileAttachments.FindAsync(fileId);
        if (attachment == null) return;

        string filesDir = Path.Combine(Directory.GetCurrentDirectory(), "Files", attachment.RoomId);
        if (!Directory.Exists(filesDir))
            Directory.CreateDirectory(filesDir);

        string finalPath = Path.Combine(filesDir, $"{fileId}_{attachment.FileName}");
        string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", fileId.ToString());

        using (FileStream fs = File.Create(finalPath))
        {
            for (int i = 0; i < totalChunks; i++)
            {
                string chunkPath = Path.Combine(tempDir, $"{i}.chunk");
                if (File.Exists(chunkPath))
                {
                    byte[] chunkData = await File.ReadAllBytesAsync(chunkPath);
                    await fs.WriteAsync(chunkData, 0, chunkData.Length);
                }
            }
        }

        Directory.Delete(tempDir, true);

        attachment.FilePath = finalPath;
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(attachment.SenderId);
        if (user == null) return;

        await Clients.Group(attachment.RoomId).SendAsync("ReceiveFileInfo",
            attachment.RoomId, user.Username, attachment.FileName, attachment.ContentType,
            attachment.FileSize, attachment.Id, attachment.SentAt);
    }

    public async Task DownloadFile(int fileId)
    {
        var file = await _db.FileAttachments.FindAsync(fileId);
        if (file == null || !File.Exists(file.FilePath))
        {
            await Clients.Caller.SendAsync("FileDownloadError", fileId);
            return;
        }
        int chunkSize = 256 * 1024;
        long fileLength = new FileInfo(file.FilePath).Length;
        int totalChunks = (int)Math.Ceiling((double)fileLength / chunkSize);
        byte[] buffer = new byte[chunkSize];

        using (FileStream fs = File.OpenRead(file.FilePath))
        {
            int chunkIndex = 0;
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                byte[] chunk = new byte[bytesRead];
                Array.Copy(buffer, chunk, bytesRead);

                await Clients.Caller.SendAsync("FileChunk", fileId, chunkIndex, totalChunks, chunk);
                chunkIndex++;
            }
        }
    }

    public async Task SearchUsers(string query)
    {
        Console.WriteLine($"Поиск: '{query}'");

        var users = _db.Users
            .Where(u => u.Username.Contains(query) || u.Nickname.Contains(query))
            .Select(u => new
            {
                Id = u.Id,
                Username = u.Username,
                Nickname = u.Nickname ?? u.Username,
                IsOnline = u.IsOnline,
                LastSeen = u.LastSeen.ToString("yyyy-MM-dd HH:mm")
            })
            .Take(20)
            .ToList();

        Console.WriteLine($"Найдено: {users.Count}");
        await Clients.Caller.SendAsync("SearchResults", users);
    }

    public async Task CreatePrivateChat(int targetUserId, string creatorName)
    {
        Console.WriteLine($"=== CreatePrivateChat ===");
        Console.WriteLine($"targetUserId: {targetUserId}, creatorName: {creatorName}");

        var creator = _db.Users.FirstOrDefault(u => u.Username == creatorName);
        var target = await _db.Users.FindAsync(targetUserId);

        Console.WriteLine($"creator найден: {creator != null}, target найден: {target != null}");

        if (creator == null || target == null)
        {
            Console.WriteLine("Ошибка: пользователь не найден!");
            return;
        }

        int minId = Math.Min(creator.Id, target.Id);
        int maxId = Math.Max(creator.Id, target.Id);
        string roomId = $"private_{minId}_{maxId}";

        Console.WriteLine($"roomId: {roomId}");

        var existingRoom = await _db.ChatRooms.FindAsync(roomId);
        Console.WriteLine($"Чат существует: {existingRoom != null}");

        if (existingRoom == null)
        {
            var room = new ChatRoom
            {
                Id = roomId,
                Name = $"@{target.Username}",
                CreatorId = creator.Id,
                CreatedAt = DateTime.UtcNow,
                IsGroup = false
            };
            _db.ChatRooms.Add(room);

            _db.RoomParticipants.Add(new RoomParticipant { RoomId = roomId, UserId = creator.Id });
            _db.RoomParticipants.Add(new RoomParticipant { RoomId = roomId, UserId = target.Id });

            await _db.SaveChangesAsync();
            Console.WriteLine("Чат создан и сохранён в БД");
        }

        await Clients.Caller.SendAsync("PrivateChatCreated", roomId, target.Username, target.Nickname ?? target.Username, true);

        if (_userConnections.ContainsKey(target.Id))
        {
            await Clients.Client(_userConnections[target.Id]).SendAsync("PrivateChatCreated", roomId, creator.Username, creator.Nickname ?? creator.Username, false);
        }
    }

    public async Task GetUserChats(string userName)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null) return;

        var chats = _db.RoomParticipants
            .Where(p => p.UserId == user.Id)
            .Join(_db.ChatRooms,
                p => p.RoomId,
                r => r.Id,
                (p, r) => new { p.RoomId, r.Name, r.IsGroup })
            .ToList();

        var result = new List<object>();

        foreach (var chat in chats)
        {
            string displayName = chat.Name;
            string otherUsername = null;

            if (!chat.IsGroup)
            {
                var otherUser = await _db.RoomParticipants
                    .Where(p => p.RoomId == chat.RoomId && p.UserId != user.Id)
                    .Join(_db.Users,
                        p => p.UserId,
                        u => u.Id,
                        (p, u) => new { u.Username, u.Nickname })
                    .FirstOrDefaultAsync();

                if (otherUser != null)
                {
                    displayName = otherUser.Nickname ?? otherUser.Username;
                    otherUsername = otherUser.Username;
                }
            }

            var lastMessage = await _db.Messages
                .Where(m => m.RoomId == chat.RoomId)
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefaultAsync();

            result.Add(new
            {
                Id = chat.RoomId,
                Name = displayName,
                OtherUsername = otherUsername,
                IsGroup = chat.IsGroup,
                LastMessageTime = lastMessage?.SentAt ?? DateTime.MinValue
            });
        }

        await Clients.Caller.SendAsync("UserChatsList", result);
    }

    public async Task Register(string userName, string publicKey = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == userName);
        if (user == null)
        {
            Context.Abort(); 
            return;
        }

        user.IsOnline = true;
        user.LastSeen = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(publicKey) && user.RSAPublicKey != publicKey)
        {
            user.RSAPublicKey = publicKey;
            await _db.SaveChangesAsync();
        }

        _connections[Context.ConnectionId] = user.Id;
        _userConnections[user.Id] = Context.ConnectionId;

        await Clients.All.SendAsync("UserJoined", userName);
        await Clients.Caller.SendAsync("CurrentNickname", user.Nickname ?? user.Username);
        await DeliverPendingMessages(user.Id);
        await GetUserChats(userName);
        await DeliverPendingKeys(user.Id);
        Console.WriteLine($"Register: {userName}, connectionId={Context.ConnectionId}, userId={user.Id}");
        Console.WriteLine($"_userConnections содержит после: {_userConnections.ContainsKey(user.Id)}");
    }

    private async Task DeliverPendingKeys(int userId)
    {
        var pendingKeys = await _db.PendingKeys
            .Where(k => k.TargetUserId == userId)
            .ToListAsync();
        Console.WriteLine($"DeliverPendingKeys: userId={userId}, найдено={pendingKeys.Count}");
        foreach (var key in pendingKeys)
        {
            Console.WriteLine($"  Отправляем ключ для комнаты {key.RoomId}");
            await Clients.Caller.SendAsync("RoomKeyReceived", key.RoomId, key.EncryptedKey);
            _db.PendingKeys.Remove(key);
        }

        await _db.SaveChangesAsync();
    }

    public async Task SendMessageToRoom(string roomId, string userName, string encryptedMessage)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == userName);
        if (user == null) return;

        var message = new Message
        {
            RoomId = roomId,
            SenderId = user.Id,
            EncryptedContent = encryptedMessage,
            SentAt = DateTime.UtcNow,
            IsDelivered = false
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        var participants = _db.RoomParticipants
            .Where(p => p.RoomId == roomId)
            .Select(p => p.UserId)
            .ToList();

        if (!participants.Contains(user.Id))
        {
            participants.Add(user.Id);
            _db.RoomParticipants.Add(new RoomParticipant
            {
                RoomId = roomId,
                UserId = user.Id,
                JoinedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        bool deliveredToSomeone = false;

        foreach (var participantId in participants)
        {
            if (participantId == user.Id)
                continue;

            if (_userConnections.TryGetValue(participantId, out string connectionId))
            {
                await Clients.Client(connectionId).SendAsync("ReceiveRoomMessage", roomId, userName, encryptedMessage, message.SentAt);
                deliveredToSomeone = true;
            }
            else
            {
                if (!_pendingMessages.ContainsKey(participantId))
                    _pendingMessages[participantId] = new List<int>();

                _pendingMessages[participantId].Add(message.Id);
            }
        }

        message.IsDelivered = deliveredToSomeone || participants.Count == 1;
        await _db.SaveChangesAsync();
        await Clients.Caller.SendAsync("ReceiveRoomMessage", roomId, userName, encryptedMessage, message.SentAt);
    }

    private async Task DeliverPendingMessages(int userId)
    {
        if (_pendingMessages.ContainsKey(userId))
        {
            var messageIds = _pendingMessages[userId];

            foreach (var messageId in messageIds)
            {
                var message = await _db.Messages.FindAsync(messageId);
                if (message != null && !message.IsDelivered)
                {
                    var sender = await _db.Users.FindAsync(message.SenderId);
                    if (sender != null && _userConnections.ContainsKey(userId))
                    {
                        await Clients.Client(_userConnections[userId]).SendAsync("ReceiveRoomMessage",
                            message.RoomId,
                            sender.Username,
                            message.EncryptedContent,
                            message.SentAt);

                        message.IsDelivered = true;
                    }
                }
            }

            await _db.SaveChangesAsync();
            _pendingMessages.Remove(userId);
        }
    }

    public async Task GetRoomHistory(string roomId)
    {
        var messages = _db.Messages
            .Where(m => m.RoomId == roomId)
            .OrderBy(m => m.SentAt)
            .Take(50)
            .ToList();

        foreach (var message in messages)
        {
            var sender = await _db.Users.FindAsync(message.SenderId);
            if (sender != null)
            {
                await Clients.Caller.SendAsync("ReceiveRoomMessage",
                    roomId,
                    sender.Username,
                    message.EncryptedContent,
                    message.SentAt);
            }
        }
    }

    public async Task JoinRoom(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await GetRoomHistory(roomId);
        await GetRoomFiles(roomId);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
    }

    public async Task CreateRoom(string roomId, string roomName, string creatorName)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == creatorName);
        if (user == null) return;

        var room = new ChatRoom
        {
            Id = roomId,
            Name = roomName,
            CreatorId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        _db.ChatRooms.Add(room);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = user.Id,
            JoinedAt = DateTime.UtcNow
        };
        _db.RoomParticipants.Add(participant);

        await _db.SaveChangesAsync();

        await Clients.All.SendAsync("RoomCreated", roomId, roomName, creatorName);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        if (_connections.TryGetValue(Context.ConnectionId, out int userId))
        {
            _connections.Remove(Context.ConnectionId);
            _userConnections.Remove(userId);

            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                user.IsOnline = false;
                user.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await Clients.All.SendAsync("UserOffline", userId, user.Username);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }
}