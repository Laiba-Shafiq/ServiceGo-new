using System;
using System.Collections.Generic;

namespace ServiceGo.Models
{
    public class BookingDetailViewModel
    {
        public string? Id { get; set; }

        public string BookingId { get; set; } = string.Empty;

        // ==================== BOOKING DETAILS ====================

        public DateTime BookingDate { get; set; }

        public string BookingTime { get; set; } = string.Empty;

        public string ServiceName { get; set; } = string.Empty;

        public string ServiceType { get; set; } = string.Empty;

        // Pending / Accepted / Rejected / Cancelled / Completed
        public string Status { get; set; } = "Pending";

        public string ProblemDescription { get; set; } = string.Empty;

        public string ProblemImage { get; set; } = string.Empty;


        // ==================== CUSTOMER DETAILS ====================

        public string CustomerName { get; set; } = string.Empty;

        public string CustomerId { get; set; } = string.Empty;

        public string CustomerPhone { get; set; } = string.Empty;

        public string Address { get; set; } = string.Empty;

        public string MapLink { get; set; } = string.Empty;


        // ==================== CUSTOMER LOCATION ====================

        public string ShopLat { get; set; } = string.Empty;

        public string ShopLng { get; set; } = string.Empty;

        public string ShopAddressInput { get; set; } = string.Empty;

        public string HomeLat { get; set; } = string.Empty;

        public string HomeLng { get; set; } = string.Empty;

        public string HomeAddressInput { get; set; } = string.Empty;


        // ==================== PROVIDER DETAILS ====================

        public string ProviderId { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;



        public string ProviderLocation { get; set; } = string.Empty;

        public string ProviderServiceType { get; set; } = string.Empty;

        public string ProviderAddress { get; set; } = string.Empty;


        // ==================== PROVIDER AVAILABILITY ====================

        public double Rating { get; set; }

        public string AvailableDays { get; set; } = string.Empty;

        public string AvailableTime { get; set; } = string.Empty;


        // ==================== PAYMENT ====================

        public decimal VisitCharges { get; set; }

    


        // ==================== RESCHEDULE ====================

        // True when there is a pending reschedule request
        public bool HasRescheduleRequest { get; set; }

        // Who sent the reschedule request?
        // Customer / Provider
        public string? RescheduleRequestedBy { get; set; }

        // Existing flags kept for compatibility
        public bool IsRescheduledByProvider { get; set; }

        public bool IsRescheduledByCustomer { get; set; }

        // New date/time requested by either side
        public DateTime? RequestedDate { get; set; }

        public string? RequestedTime { get; set; }

        // Last accepted rescheduled date/time
        public DateTime? RescheduledDate { get; set; }

        public string? RescheduledTime { get; set; }

        // Pending / Accepted / Rejected
        public string? RescheduledStatus { get; set; }


        // ==================== CANCELLATION ====================

        // Customer / Provider
        public string? CancelledBy { get; set; }

        public DateTime? CancelledAt { get; set; }


        // ==================== REJECTION ====================

        public DateTime? RejectedAt { get; set; }


        // ==================== COMPLETION ====================

        public DateTime? CompletedAt { get; set; }


        // ==================== RATING & REVIEW ====================

        public int? RatingGiven { get; set; }

        public string? ReviewComment { get; set; }
    }


    // =========================================================
    // CUSTOMER DASHBOARD
    // =========================================================

    public class CustomerDashboardViewModel
    {
        public string CustomerName { get; set; } = string.Empty;

        public string CustomerEmail { get; set; } = string.Empty;

        public string CustomerPhone { get; set; } = string.Empty;

        public string CustomerId { get; set; } = string.Empty;

        public List<BookingDetailViewModel> Bookings { get; set; }
            = new List<BookingDetailViewModel>();
    }
}