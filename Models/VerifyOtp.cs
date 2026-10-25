using System.ComponentModel.DataAnnotations;

namespace ServiceGo.Models
{
    public class VerifyOtp
    {
        [Required(ErrorMessage = "OTP is required.")]
        [StringLength(6, MinimumLength = 6, ErrorMessage = "OTP must be exactly 6 digits.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "OTP must contain exactly 6 digits.")]
        public string Otp { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;
    }
}
