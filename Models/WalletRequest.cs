using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class WalletRequest
    {
        public int Id { get; set; }
        [MaxLength(20)]
        public string ProviderId { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string ReceiptImagePath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "Pending", "Approved", "Rejected"
        public DateTime RequestDate { get; set; }
    }
}
