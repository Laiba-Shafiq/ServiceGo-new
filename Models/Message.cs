using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class Message
    {
        [Key]
        public int MessageId { get; set; }

        [Required]
        public string? SenderId { get; set; }

        [Required]
        public string? ReceiverId { get; set; }

        [Required]
        public string? SenderName { get; set; }

        public string? TextContent { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public bool IsFromCustomer { get; set; }

        public string? AudioUrl { get; set; }

        // WhatsApp-style deletion state. The message row is kept in the
        // database; these flags control what each participant can see.
        public bool DeletedForSender { get; set; } = false;
        public bool DeletedForReceiver { get; set; } = false;
        public bool DeletedForEveryone { get; set; } = false;
    }
}
