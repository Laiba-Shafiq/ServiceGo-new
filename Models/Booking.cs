using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

namespace ServiceGo.Models
{
    public class Booking
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string CustomerName { get; set; } = string.Empty;

        [Required]
        public string CustomerID { get; set; } = string.Empty;

       

        public string? CustomerEmail { get; set; }

        public string? BookingId { get; set; }

        [Required]
        public string ServiceName { get; set; } = string.Empty;

        [Required]
        public string ServiceType { get; set; } = string.Empty;

        [Required]
        public string Address { get; set; } = string.Empty;

        [Required]
        public string Coordinates { get; set; } = string.Empty;

        // Current / confirmed booking date and time
        [Required]
        public DateTime BookingDate { get; set; }

        [Required]
        public string BookingTime { get; set; } = string.Empty;

        [Required]
        public string ProblemDetails { get; set; } = string.Empty;

        public string? Distance { get; set; }

        // Money fields
        public decimal? VisitCharges { get; set; }


 
        [Required]
        public int ProviderId { get; set; }

        public string? ProviderName { get; set; }


        // ==================== BOOKING STATUS ====================

        // Pending / Accepted / Rejected / Cancelled / Completed
        public string Status { get; set; } = "Pending";

        public DateTime? CompletedAt { get; set; }

        public DateTime? CancelledAt { get; set; }

        // Customer / Provider
        public string? CancelledBy { get; set; }

        public DateTime? RejectedAt { get; set; }


        // ==================== RESCHEDULE ====================

        // Kis side ne reschedule request bheji?
        // Values: Customer / Provider
        public string? RescheduleRequestedBy { get; set; }

        // Existing fields kept for compatibility
        public bool IsRescheduledByProvider { get; set; } = false;

        public bool IsRescheduledByCustomer { get; set; } = false;

        // New date/time requested by customer or provider
        public DateTime? RequestedDate { get; set; }

        public string? RequestedTime { get; set; }

        // Last accepted rescheduled date/time
        public DateTime? RescheduledDate { get; set; }

        public string? RescheduledTime { get; set; }

        // Pending / Accepted / Rejected
        public string? RescheduledStatus { get; set; }
        public bool WalletCommissionDeducted { get; set; } = false;


        // ==================== RATING & REVIEW ====================

        public int? Rating { get; set; }

        public string? ReviewComment { get; set; }


        // ==================== PROBLEM IMAGE =========   ===========

        [JsonIgnore]
        public string? ImagePath { get; set; }

        [NotMapped]
        public IFormFile? ProblemImage { get; set; }
    }
}