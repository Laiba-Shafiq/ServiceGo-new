using System;
using System.Collections.Generic;

namespace ServiceGo.Models
{
    // =========================================================
    // CUSTOMER HISTORY PAGE VIEW MODELS
    // =========================================================

    public class CustomerHistoryViewModel
    {
        public List<CustomerJobHistoryItem> JobHistory { get; set; }
            = new List<CustomerJobHistoryItem>();

        public List<CustomerRatingHistoryItem> RatingHistory { get; set; }
            = new List<CustomerRatingHistoryItem>();

        public List<ComplaintModel> ComplaintHistory { get; set; }
            = new List<ComplaintModel>();
    }

    public class CustomerJobHistoryItem
    {
        public int Id { get; set; }

        public string BookingId { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public string ServiceName { get; set; } = string.Empty;

        public string ServiceType { get; set; } = string.Empty;

        public string Address { get; set; } = string.Empty;

        public string ProblemDetails { get; set; } = string.Empty;

        public DateTime BookingDate { get; set; }

        public string BookingTime { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime? CompletedAt { get; set; }

        public DateTime? CancelledAt { get; set; }

        public string? CancelledBy { get; set; }

        public DateTime? RejectedAt { get; set; }

        // Reschedule information
        public string? RescheduleRequestedBy { get; set; }

        public DateTime? RequestedDate { get; set; }

        public string? RequestedTime { get; set; }

        public DateTime? RescheduledDate { get; set; }

        public string? RescheduledTime { get; set; }

        public string? RescheduledStatus { get; set; }

        public bool HasRescheduleHistory { get; set; }

        public decimal? VisitCharges { get; set; }


    }

    public class CustomerRatingHistoryItem
    {
        public int BookingDatabaseId { get; set; }

        public string BookingId { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public string ServiceName { get; set; } = string.Empty;

        public string ServiceType { get; set; } = string.Empty;

        public DateTime BookingDate { get; set; }

        public int Rating { get; set; }

        public string ReviewComment { get; set; } = string.Empty;
    }
}
