namespace ServiceGo.Models
{
    public class ActiveCustomerViewModel
    {
        public int CustomerId { get; set; }

        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? ParmenantAddress { get; set; }
        public string? JoinedDate { get; set; }
        public string? Status { get; set; }
        public string? City { get; set; }
        public string? ProfileImage { get; set; }
    }
}