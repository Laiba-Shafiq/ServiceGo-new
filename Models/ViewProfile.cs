namespace ServiceGo.Models
{
    public class Viewprofile
    {
        // Provider identification
        public int ProviderId { get; set; } // Register model 'Id' ke barabar

        // Profile Display Info
        public string? FullName { get; set; } // Register model 'Name'
        public string? ProfileImage { get; set; } // Register model 'ProfilePicPath'
        public string? ProviderCode { get; set; } // Register model 'ProviderId' (string version)

        // Overview Stats
        public double Rating { get; set; }
        public int ReviewsCount { get; set; }
        public string? Status { get; set; }

        // Professional Details
        public string? ServiceCategory { get; set; } // Register 'SelectedServices'

        public string? ServiceType { get; set; } // Register 'SelectedServiceTypes'

        // Availability
        public string? WorkingDays { get; set; } // Register 'AvailableDays'
        public string? AvailableHours { get; set; } // Register combo 'StartTime' - 'EndTime'

        // Contact & Location
        public string? CityHub { get; set; } // Register 'City'
        public string? ContactNumber { get; set; } // Register 'PhoneNumber'

        // Shop Location Details (Matching Register Model)
        public string? ShopAddress { get; set; } // Map this to Register.ShopAddressInput
        public string? ShopLat { get; set; }
        public string? ShopLng { get; set; }

        // Home Location Details (Matching Register Model)
        public string? HomeAddress { get; set; } // Map this to Register.HomeAddressInput
        public string? HomeLat { get; set; }
        public string? HomeLng { get; set; }
    }
}