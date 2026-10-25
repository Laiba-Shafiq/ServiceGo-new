using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class Forget
    {
        [Required(ErrorMessage = "Email address is required.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [Display(Name = "Email Address")]
        public string? Email { get; set; }

        public string? OtpCode { get; set; }
        public DateTime? OtpGeneratedAt { get; set; }
    }
}
