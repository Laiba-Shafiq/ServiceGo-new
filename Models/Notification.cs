using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class Notification
    {
        [Key]
        public int NotificationId { get; set; }
        public string? UserId { get; set; }
        public string? TargetUserId { get; set; }
        public string? SenderUserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string NotificationType { get; set; } = "System";
        public string IconClass { get; set; } = "fa-solid fa-bell";
        public bool IsRead { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int? BookingId { get; set; }
        public string? ReferenceId { get; set; }
        public string? ActionUrl { get; set; }
    }
}
