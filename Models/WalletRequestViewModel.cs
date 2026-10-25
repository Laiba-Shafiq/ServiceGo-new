namespace ServiceGo.Models
{
    public class WalletRequestViewModel
    {
        public int Id { get; set; }
        public string ProviderFormattedId { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ReceiptImagePath { get; set; } = string.Empty;
    }
}
