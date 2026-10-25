namespace ServiceGo.Models
{
    public class CustomerSignup
    {
        public int Id { get; set; }

        // Account data entered during Customer Signup.
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }
    

        public string? CustomerId { get; set; }

        // Account status used by Admin for Block/Unblock.
        public string Status { get; set; } = "Active";
       

        // =========================================================
        // PERMANENT CUSTOMER PROFILE DATA
        // These values are independent of Booking.
        // =========================================================

        public string? Phone { get; set; }
        public string? City { get; set; }
        public string? Address { get; set; }
        public string? ProfileImage { get; set; }
        public DateTime? JoinedDate { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
