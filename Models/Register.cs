using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ServiceGo.Models
{
    public class Register
    {
        [Key]
        public int Id { get; set; }

        [Required(ErrorMessage = "Name is required. Please fill this field.")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Father Name is required. Please fill this field.")]
        public string FatherName { get; set; } = string.Empty;

        public string? ProviderId { get; set; }

        [Required(ErrorMessage = "CNIC is required. Please fill this field.")]
        public string CNIC { get; set; } = string.Empty;

        [Required(ErrorMessage = "Phone Number is required. Please fill this field.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "City is required. Please fill this field.")]
        public string City { get; set; } = string.Empty;

        [Required(ErrorMessage = "Local Address is required. Please fill this field.")]
        public string LocalAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "Permanent Address is required. Please fill this field.")]
        public string PermanentAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please select at least one service.")]
        public string SelectedServices { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please select at least one service type.")]
        public string SelectedServiceTypes { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please select at least one available day.")]
        public string AvailableDays { get; set; } = string.Empty;

        [Required(ErrorMessage = "Start Time is required. Please fill this field.")]
        public string StartTime { get; set; } = string.Empty;

        [Required(ErrorMessage = "End Time is required. Please fill this field.")]
        public string EndTime { get; set; } = string.Empty;

        public string? ShopAddressInput { get; set; }
        public double? ShopLat { get; set; }
        public double? ShopLng { get; set; }

        public string? HomeAddressInput { get; set; }
        public double? HomeLat { get; set; }
        public double? HomeLng { get; set; }

        public string? SignupId { get; set; }
        public string? Email { get; set; }

        [NotMapped] public IFormFile? ProfileInput { get; set; }
        [NotMapped] public IFormFile? CNICFrontInput { get; set; }
        [NotMapped] public IFormFile? CNICBackInput { get; set; }
        [NotMapped] public IFormFile? Certificate { get; set; }
        [NotMapped] public IFormFile? Affidavit { get; set; }
        [NotMapped] public IFormFile? ProviderVideo { get; set; }

        public string? CNICFrontPath { get; set; }
        public string? CNICBackPath { get; set; }
        public string? ProfilePicPath { get; set; }
        public string? CertificatePath { get; set; }
        public string? AffidavitPath { get; set; }
        public string? VideoPath { get; set; }

        public bool IsApproved { get; set; } = false;
        public bool IsRejected { get; set; } = false;
        public bool IsBlocked { get; set; } = false;
        public string? RejectionReason { get; set; }
  

        public string Role { get; set; } = "Provider";
        public double Rating { get; set; } = 0;
        public int ReviewsCount { get; set; } = 0;
        public decimal WalletBalance { get; set; } = 20.00m;

        [NotMapped]
        public string Status => IsBlocked ? "Blocked" : (IsApproved ? "Approved" : (IsRejected ? "Rejected" : "Pending"));
    }
}
