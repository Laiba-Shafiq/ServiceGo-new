using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class CustomerProfileViewModel
    {
        public string CustomerId { get; set; } = string.Empty;

        // Account information created during signup.
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        // Permanent customer profile information.
        [Required(ErrorMessage = "Phone number is required.")]
        public string Phone { get; set; } = string.Empty;

        [Required(ErrorMessage = "City is required.")]
        public string City { get; set; } = string.Empty;

        [Required(ErrorMessage = "Address is required.")]
        public string Address { get; set; } = string.Empty;

        public string? ProfileImage { get; set; }

        public string JoinedDate { get; set; } = string.Empty;

        // Optional saved location for the customer's permanent profile.
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
