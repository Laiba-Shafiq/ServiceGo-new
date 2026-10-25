using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace ServiceGo.Models
{
    public class ProviderProfile
    {
        // --- Primary & Identity Details ---
        public int Id { get; set; }
        public string? ProviderId { get; set; }

        public string Name { get; set; } = string.Empty;

        // Alias for controller/view compatibility
        [NotMapped]
        public string FullName
        {
            get => Name;
            set => Name = value;
        }

        public string FatherName { get; set; } = string.Empty;

        public string CNIC { get; set; } = string.Empty;

        [NotMapped]
        public string CnicNumber
        {
            get => CNIC;
            set => CNIC = value;
        }

        public string PhoneNumber { get; set; } = string.Empty;

        [NotMapped]
        public string Phone
        {
            get => PhoneNumber;
            set => PhoneNumber = value;
        }

        public string City { get; set; } = string.Empty;
        public string LocalAddress { get; set; } = string.Empty;
        public string PermanentAddress { get; set; } = string.Empty;
        public string? Email { get; set; }

        // Combined Location property for Dashboard / Controllers
        [NotMapped]
        private string _location = string.Empty;

        // Combined Location property with both get and set
        [NotMapped]
        public string Location
        {
            get => string.IsNullOrEmpty(_location)
                ? (string.IsNullOrEmpty(City) ? LocalAddress : $"{City}, {LocalAddress}")
                : _location;
            set => _location = value;
        }

        // --- Professional Details ---
        public string SelectedServices { get; set; } = string.Empty;
        public string SelectedServiceTypes { get; set; } = string.Empty;
  

        // Shop & Home Location Details
        public string? ShopAddressInput { get; set; }
        public double? ShopLat { get; set; }
        public double? ShopLng { get; set; }

        public string? HomeAddressInput { get; set; }
        public double? HomeLat { get; set; }
        public double? HomeLng { get; set; }

        // --- Availability ---
        public string AvailableDays { get; set; } = string.Empty;
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;

        // --- Existing Files (Paths from DB) ---
        public string? ProfilePicPath { get; set; }
        public string? CNICFrontPath { get; set; }
        public string? CNICBackPath { get; set; }
        public string? CertificatePath { get; set; }
        public string? AffidavitPath { get; set; }
        public string? VideoPath { get; set; }

        // --- New Upload Files (NotMapped for Update) ---
        [NotMapped] public IFormFile? ProfileInput { get; set; }
        [NotMapped] public IFormFile? CNICFrontInput { get; set; }
        [NotMapped] public IFormFile? CNICBackInput { get; set; }
        [NotMapped] public IFormFile? Certificate { get; set; }
        [NotMapped] public IFormFile? Affidavit { get; set; }
        [NotMapped] public IFormFile? ProviderVideo { get; set; }

        // --- Additional Dashboard Info & Metrics ---
        public string Status { get; set; } = "Pending";
        public double Rating { get; set; } = 0;
        public int ReviewsCount { get; set; } = 0;

        // Extra properties referenced in your controller/dashboard errors
        [NotMapped] public int PendingApprovalsCount { get; set; } = 0;
        [NotMapped] public decimal WalletBalance { get; set; } = 0;
    }
}