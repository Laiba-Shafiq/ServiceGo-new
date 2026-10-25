
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ServiceGo.Models
{
    [Table("Complaints")]
    public class ComplaintModel
    {
        // =====================================================
        // PRIMARY KEY
        // =====================================================

        [Key]
        public int Id { get; set; }


        // =====================================================
        // COMPLAINT INFORMATION
        // =====================================================

        public string ComplaintId { get; set; } = string.Empty;

        public string CustomerEmail { get; set; } = string.Empty;

        public string ProviderId { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public string CustomerId { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        public string ComplaintType { get; set; } = string.Empty;

        public string IssueCategory { get; set; } = string.Empty;

        public string OtherIssue { get; set; } = string.Empty;

        public string ComplaintDetails { get; set; } = string.Empty;


        // =====================================================
        // DATE & STATUS
        // =====================================================

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /*
         * Possible Status:
         *
         * Pending
         * Warning Sent
         * Resolved
         * Blocked
         */

        public string Status { get; set; } = "Pending";


        // =====================================================
        // WARNING INFORMATION
        // =====================================================

        public bool WarningSent { get; set; } = false;


        // =====================================================
        // RUNTIME TARGET USER INFORMATION
        // =====================================================
        //
        // These properties are NOT stored in the database.
        // They are filled by AdminController when complaints
        // are displayed on the Admin Complaint page.
        //
        // Customer vs Provider:
        //     Target = Provider
        //
        // Provider vs Customer:
        //     Target = Customer
        // =====================================================

        [NotMapped]
        public string TargetRole { get; set; } = string.Empty;

        [NotMapped]
        public string TargetUserId { get; set; } = string.Empty;

        [NotMapped]
        public string TargetUserName { get; set; } = string.Empty;

        [NotMapped]
        public string TargetPhone { get; set; } = string.Empty;

        [NotMapped]
        public string TargetEmail { get; set; } = string.Empty;

        [NotMapped]
        public bool TargetIsBlocked { get; set; } = false;


        // =====================================================
        // COMPLAINT HISTORY
        // =====================================================
        //
        // These values are calculated at runtime.
        // They are NOT database columns.
        //
        // Current complaint is NOT counted in these values.
        // =====================================================


        // -----------------------------------------------------
        // PREVIOUS COMPLAINTS
        // -----------------------------------------------------

        [NotMapped]
        public int PreviousComplaintsCount { get; set; } = 0;


        // -----------------------------------------------------
        // PREVIOUS WARNINGS
        // -----------------------------------------------------

        [NotMapped]
        public int PreviousWarningsCount { get; set; } = 0;


        // -----------------------------------------------------
        // PREVIOUS BLOCKS
        // -----------------------------------------------------

        [NotMapped]
        public int PreviousBlockedCount { get; set; } = 0;
    }
}

