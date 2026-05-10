using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;

// МОДЕЛИ ДАННЫХ

public class User
{
    [Key]
    public int Id { get; set; }
    public string Username { get; set; }
    public string? PasswordHash { get; set; }
    public string Nickname { get; set; }
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public byte[]? AvatarData { get; set; }
    public string? RSAPublicKey { get; set; }
}

public class PendingKey
{
    [Key]
    public int Id { get; set; }
    public int TargetUserId { get; set; }
    public string RoomId { get; set; }
    public byte[] EncryptedKey { get; set; }
}

public class Message
{
    [Key]
    public int Id { get; set; }
    public string RoomId { get; set; }
    public int SenderId { get; set; }
    public string EncryptedContent { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsDelivered { get; set; } = false;
}

public class ChatRoom
{
    [Key]
    public string Id { get; set; }
    public string Name { get; set; }
    public int CreatorId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsGroup { get; set; } = true;
}

public class RoomParticipant
{
    [Key]
    public int Id { get; set; }
    public string RoomId { get; set; }
    public int UserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class FileAttachment
{
    [Key]
    public int Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long FileSize { get; set; }
    public string FilePath { get; set; }  // Путь к файлу на диске
    public string RoomId { get; set; }
    public int SenderId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public int TotalChunks { get; set; }
}

public class FileChunk
{
    [Key]
    public int Id { get; set; }
    public int FileId { get; set; }
    public int ChunkIndex { get; set; }
    public byte[] Data { get; set; }
    public int TotalChunks { get; set; }
}

// КОНТЕКСТ БАЗЫ ДАННЫХ

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<ChatRoom> ChatRooms { get; set; }
    public DbSet<RoomParticipant> RoomParticipants { get; set; }
    public DbSet<FileAttachment> FileAttachments { get; set; }
    public DbSet<FileChunk> FileChunks { get; set; }
    public DbSet<PendingKey> PendingKeys { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=chat.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Уникальный индекс на Username
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();
    }
}