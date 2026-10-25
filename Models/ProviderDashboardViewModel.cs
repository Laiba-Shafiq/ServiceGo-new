using System;
using System.Collections.Generic;

namespace ServiceGo.Models
{
    public class ProviderDashboardViewModel
    {
        public int Id { get; set; }

        public string BookingId { get; set; } = string.Empty;

        // ==================== CUSTOMER DETAILS ====================

        public string CustomerFormattedId { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        // Customer phone is intentionally NOT included.
        // Provider-side dashboard should use the customer ID, not expose
        // the customer's personal phone number.

        // ==================== SERVICE DETAILS ====================

        public string ServiceName { get; set; } = string.Empty;

        public string ServiceType { get; set; } = string.Empty;

        public string IssueDescription { get; set; } = string.Empty;

        public string IssueImage { get; set; } = string.Empty;

        // ==================== LOCATION & DISTANCE ====================

        public string Location { get; set; } = string.Empty;

        public double? Lat { get; set; }

        public double? Lng { get; set; }

        public string Distance { get; set; } = string.Empty;

        // ==================== FINANCIAL DETAILS ====================

        public decimal VisitCharge { get; set; }

    

        public decimal WalletBalance { get; set; }

        public bool CanAcceptBooking { get; set; }

        public string? WalletMessage { get; set; }

        // ==================== DATE & TIME ====================

        public string Date { get; set; } = string.Empty;

        public string Time { get; set; } = string.Empty;

        // ==================== BOOKING STATUS ====================

        public string Status { get; set; } = "Pending";

        public DateTime? CompletedAt { get; set; }

        public DateTime? CancelledAt { get; set; }

        public string? CancelledBy { get; set; }

        public DateTime? RejectedAt { get; set; }

        // ==================== RESCHEDULE ====================

        public bool HasRescheduleRequest { get; set; }

        public string? RescheduleRequestedBy { get; set; }

        public string? RequestedNewDate { get; set; }

        public string? RequestedNewTime { get; set; }

        // Existing compatibility fields are retained because the
        // current ProviderController/dashboard logic still uses them.
        public bool IsRescheduledByCustomer { get; set; }

        public bool IsRescheduledByProvider { get; set; }

        public DateTime? RequestedDate { get; set; }

        public string? RequestedTime { get; set; }

        public DateTime? RescheduledDate { get; set; }

        public string? RescheduledTime { get; set; }

        public string? RescheduledStatus { get; set; }

        public bool WalletCommissionDeducted { get; set; } = false;

        // ==================== ACTIONS ====================

        public bool CanMarkAsComplete { get; set; }

        public bool CanReschedule { get; set; }

        public bool CanReject { get; set; }
    }

    public class ProviderDashboardMainViewModel
    {
        public string FullName { get; set; } = string.Empty;

        public string ProviderId { get; set; } = string.Empty;

        public string Location { get; set; } = string.Empty;

        // This Phone belongs to the PROVIDER, not the customer.
        public string Phone { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public decimal WalletBalance { get; set; } = 10;

        public int PendingApprovalsCount { get; set; }

        public List<ProviderDashboardViewModel> CurrentJobs { get; set; }
            = new List<ProviderDashboardViewModel>();

        public List<ProviderDashboardViewModel> AcceptedBookings { get; set; }
            = new List<ProviderDashboardViewModel>();
    }
}
