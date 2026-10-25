using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class RescheduleRequest
    {
        public int BookingId { get; set; }

        public int? Id { get; set; }

        [Required]
        public DateTime? NewDate { get; set; }

        [Required]
        public string? NewTime { get; set; }
    }
}
