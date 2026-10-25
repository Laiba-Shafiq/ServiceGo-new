using System;
using System.Collections.Generic;

namespace ServiceGo.Models
{
    public class ProviderJobHistoryItem
    {
        public int Id { get; set; }

        public string BookingId { get; set; } = string.Empty;

        // Customer identity only; customer phone/email are intentionally
        // not part of the provider history model.
        public string CustomerId { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        // Service
        public string ServiceName { get; set; } = string.Empty;

        public string ServiceType { get; set; } = string.Empty;

        public string ProblemDetails { get; set; } = string.Empty;

        // Location
        public string Address { get; set; } = string.Empty;

        // Booking date/time
        public DateTime BookingDate { get; set; }

        public string BookingTime { get; set; } = string.Empty;

        // Status
        public string Status { get; set; } = "Unknown";

        // Completion
        public DateTime? CompletedAt { get; set; }

        // Cancellation
        public DateTime? CancelledAt { get; set; }

        public string? CancelledBy { get; set; }

        // Rejection
        public DateTime? RejectedAt { get; set; }

        // Reschedule
        public bool HasRescheduleHistory { get; set; }

        public string? RescheduleRequestedBy { get; set; }

        public DateTime? RequestedDate { get; set; }

        public string? RequestedTime { get; set; }

        public DateTime? RescheduledDate { get; set; }

        public string? RescheduledTime { get; set; }

        public string? RescheduledStatus { get; set; }

        // Payment
        public decimal? VisitCharges { get; set; }


    }

    public class ProviderHistoryViewModel
    {
        public string ProviderName { get; set; } = string.Empty;

        public string ProviderId { get; set; } = string.Empty;

        public List<ProviderJobHistoryItem> JobHistory { get; set; }
            = new List<ProviderJobHistoryItem>();

        public List<ComplaintModel> ComplaintHistory { get; set; }
            = new List<ComplaintModel>();
    }
}
