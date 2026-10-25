using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json; // Ye line add karein
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ServiceGo.Data;
using ServiceGo.Models;
using ForgetModel = ServiceGo.Models.Forget;
using RegisterModel = ServiceGo.Models.Register;
using ResetPasswordModel = ServiceGo.Models.ResetPassword;
using VerifyOtpModel = ServiceGo.Models.VerifyOtp;
namespace ServiceGo.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _context; // Field

        // ---------------------------------------------------------
        // PASSWORD HASHING (PBKDF2 + random salt, built into ASP.NET Core)
        // The database stores only the hash, never the real password.
        // ---------------------------------------------------------
        private static readonly PasswordHasher<string> _passwordHasher = new PasswordHasher<string>();

        private static string HashPassword(string plainPassword)
        {
            return _passwordHasher.HashPassword("user", plainPassword);
        }

        private static bool IsHashedPassword(string? stored)
        {
            // ASP.NET Core Identity v3 hashes always start with "AQAAAA".
            return !string.IsNullOrEmpty(stored) &&
                   stored.StartsWith("AQAAAA", StringComparison.Ordinal);
        }

        // Returns true when the entered password is correct.
        // Old accounts that still have a plain-text password keep working,
        // and are converted to a hash by the caller (needsUpgrade = true).
        private static bool VerifyPassword(string? stored, string entered, out bool needsUpgrade)
        {
            needsUpgrade = false;

            if (string.IsNullOrEmpty(stored))
                return false;

            if (IsHashedPassword(stored))
            {
                var result = _passwordHasher.VerifyHashedPassword("user", stored, entered);

                if (result == PasswordVerificationResult.SuccessRehashNeeded)
                    needsUpgrade = true;

                return result != PasswordVerificationResult.Failed;
            }

            // Legacy plain-text password (created before hashing was added).
            bool same = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(stored),
                System.Text.Encoding.UTF8.GetBytes(entered));

            needsUpgrade = same;
            return same;
        }
        private readonly IWebHostEnvironment _environment;
        private readonly IDataProtector _signupVerificationProtector;
        // Yahan ye constructor hona CHAHIYE
        private readonly string _smtpServer;
        private readonly int _smtpPort;
        private readonly string _senderEmail;
        private readonly string _senderName;
        private readonly string _senderPassword;
        private readonly bool _enableSsl;

        public HomeController(
            AppDbContext context,
            IWebHostEnvironment environment,
            IConfiguration configuration,
            IDataProtectionProvider dataProtectionProvider)
        {
            _context = context;
            _environment = environment;
            _signupVerificationProtector =
                dataProtectionProvider.CreateProtector("ServiceGo.SignupVerification.v1");
            _smtpServer = configuration["EmailSettings:SmtpServer"] ?? "smtp.gmail.com";
            _smtpPort = int.TryParse(configuration["EmailSettings:SmtpPort"], out var port) ? port : 587;
            _senderEmail = configuration["EmailSettings:SenderEmail"] ?? string.Empty;
            _senderName = configuration["EmailSettings:SenderName"] ?? "ServiceGo";
            _senderPassword = configuration["EmailSettings:Password"] ?? string.Empty;
            _enableSsl = !bool.TryParse(configuration["EmailSettings:EnableSsl"], out var ssl) || ssl;
        }

        [HttpGet]
        public async Task<IActionResult> Register(int? id = null, string? email = null)
        {
            // =========================================================
            // RE-REGISTRATION: keep the existing rejected record flow
            // =========================================================
            if (id.HasValue && id.Value > 0)
            {
                var rejected = await _context.Register.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == id.Value && r.IsRejected);

                if (rejected != null)
                {
                    ViewBag.IsReRegistration = true;
                    ViewBag.RejectionReason = rejected.RejectionReason;
                    ViewBag.ProviderId = rejected.ProviderId;
                    ViewBag.Email = rejected.Email;

                    return View(new RegisterModel
                    {
                        Id = rejected.Id,
                        ProviderId = rejected.ProviderId,
                        SignupId = rejected.SignupId,
                        Email = rejected.Email,
                        Role = "Provider"
                    });
                }
            }

            // Existing email-based re-registration flow.
            if (!string.IsNullOrWhiteSpace(email))
            {
                string normalizedEmail = email.Trim().ToLowerInvariant();

                var rejectedByEmail = await _context.Register.AsNoTracking()
                    .Where(r => r.Email != null && r.IsRejected)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync(r => r.Email!.Trim().ToLower() == normalizedEmail);

                if (rejectedByEmail != null)
                {
                    ViewBag.IsReRegistration = true;
                    ViewBag.RejectionReason = rejectedByEmail.RejectionReason;
                    ViewBag.ProviderId = rejectedByEmail.ProviderId;
                    ViewBag.Email = rejectedByEmail.Email;

                    return View(new RegisterModel
                    {
                        Id = rejectedByEmail.Id,
                        ProviderId = rejectedByEmail.ProviderId,
                        SignupId = rejectedByEmail.SignupId,
                        Email = rejectedByEmail.Email,
                        Role = "Provider"
                    });
                }
            }

            // =========================================================
            // NORMAL PROVIDER REGISTRATION AFTER EMAIL VERIFICATION
            // The verification action stores VerifiedProviderSignupId in
            // Session. Load the actual ProviderSignup row here so the
            // Complete Registration form is NEVER blank.
            // =========================================================
            int? verifiedProviderId =
                HttpContext.Session.GetInt32("VerifiedProviderSignupId");

            // Fallback for an already logged-in provider.
            if (!verifiedProviderId.HasValue)
            {
                var role = HttpContext.Session.GetString("UserRole");
                if (string.Equals(role, "Provider", StringComparison.OrdinalIgnoreCase))
                    verifiedProviderId = HttpContext.Session.GetInt32("UserId");
            }

            if (verifiedProviderId.HasValue && verifiedProviderId.Value > 0)
            {
                var provider = await _context.ProviderSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == verifiedProviderId.Value);

                if (provider != null)
                {
                    string publicProviderId = $"PRO-{provider.Id}";

                    // Keep these in session too, so the same provider identity
                    // remains available during the registration POST.
                    HttpContext.Session.SetInt32("VerifiedProviderSignupId", provider.Id);
                    HttpContext.Session.SetString("VerifiedProviderEmail", provider.Email ?? string.Empty);

                    ViewBag.IsReRegistration = false;
                    ViewBag.ProviderId = publicProviderId;
                    ViewBag.Email = provider.Email;

                    return View(new RegisterModel
                    {
                        ProviderId = publicProviderId,
                        SignupId = provider.Id.ToString(),
                        Name = provider.Name,
                        Email = provider.Email,
                        Role = "Provider"
                    });
                }
            }

            // Backward-compatible fallback for the older TempData flow.
            var tempProviderId = TempData["ProviderId"]?.ToString();
            var tempEmail = TempData["Email"]?.ToString();

            if (int.TryParse(tempProviderId, out int numericProviderId))
                tempProviderId = $"PRO-{numericProviderId}";

            ViewBag.IsReRegistration = false;
            ViewBag.ProviderId = tempProviderId;
            ViewBag.Email = tempEmail;

            return View(new RegisterModel
            {
                ProviderId = tempProviderId,
                SignupId = tempProviderId?.Replace("PRO-", ""),
                Email = tempEmail,
                Role = "Provider"
            });
        }

        [HttpPost]
        [RequestSizeLimit(209715200)] // 200 MB total request (video up to 50MB + images)
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200, ValueLengthLimit = int.MaxValue, MultipartHeadersLengthLimit = int.MaxValue)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register()
        {
            try
            {
                if (!Request.HasFormContentType)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid registration request."
                    });
                }

                var form = await Request.ReadFormAsync();

                string Get(string key)
                {
                    return form[key].FirstOrDefault()?.Trim() ?? string.Empty;
                }

                string? GetNullable(string key)
                {
                    var value = form[key].FirstOrDefault();
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }

                int.TryParse(Get("Id"), out int id);

                var model = new RegisterModel
                {
                    Id = id,
                    Name = Get("Name"),
                    FatherName = Get("FatherName"),
                    ProviderId = GetNullable("ProviderId"),
                    SignupId = GetNullable("SignupId"),
                    Email = GetNullable("Email"),
                    CNIC = Get("CNIC"),
                    PhoneNumber = Get("PhoneNumber"),
                    City = Get("City"),
                    LocalAddress = Get("LocalAddress"),
                    PermanentAddress = Get("PermanentAddress"),
                    SelectedServices = Get("SelectedServices"),
                    SelectedServiceTypes = Get("SelectedServiceTypes"),
                    AvailableDays = Get("AvailableDays"),
                    StartTime = Get("StartTime"),
                    EndTime = Get("EndTime"),
                    ShopAddressInput = GetNullable("ShopAddressInput"),
                    HomeAddressInput = GetNullable("HomeAddressInput"),
                    Role = "Provider"
                };

                if (double.TryParse(
                    Get("ShopLat"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var shopLat))
                    model.ShopLat = shopLat;

                if (double.TryParse(
                    Get("ShopLng"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var shopLng))
                    model.ShopLng = shopLng;

                if (double.TryParse(
                    Get("HomeLat"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var homeLat))
                    model.HomeLat = homeLat;

                if (double.TryParse(
                    Get("HomeLng"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var homeLng))
                    model.HomeLng = homeLng;

                // File names MUST match the Register model properties exactly.
                model.ProfileInput = form.Files.GetFile(nameof(RegisterModel.ProfileInput));
                model.CNICFrontInput = form.Files.GetFile(nameof(RegisterModel.CNICFrontInput));
                model.CNICBackInput = form.Files.GetFile(nameof(RegisterModel.CNICBackInput));
                model.Certificate = form.Files.GetFile(nameof(RegisterModel.Certificate));
                model.Affidavit = form.Files.GetFile(nameof(RegisterModel.Affidavit));
                model.ProviderVideo = form.Files.GetFile(nameof(RegisterModel.ProviderVideo));

                bool isReRegistration = model.Id > 0;
                RegisterModel? existing = null;

                if (isReRegistration)
                {
                    existing = await _context.Register
                        .FirstOrDefaultAsync(r => r.Id == model.Id && r.IsRejected);

                    if (existing == null)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Previous rejected registration could not be found."
                        });
                    }

                    // Identity is taken from the database, not from hidden inputs.
                    model.ProviderId = existing.ProviderId;
                    model.SignupId = existing.SignupId;
                    model.Email = existing.Email;
                }

                var errors = new List<object>();

                void Required(string field, string? value, string message)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        errors.Add(new
                        {
                            Field = field,
                            Errors = new[] { message }
                        });
                    }
                }

                Required(nameof(RegisterModel.Name), model.Name, "Full Name is required.");
                Required(nameof(RegisterModel.FatherName), model.FatherName, "Father Name is required.");
                Required(nameof(RegisterModel.Email), model.Email, "Email is required.");
                Required(nameof(RegisterModel.CNIC), model.CNIC, "CNIC is required.");
                Required(nameof(RegisterModel.PhoneNumber), model.PhoneNumber, "Phone Number is required.");
                Required(nameof(RegisterModel.City), model.City, "City is required.");
                Required(nameof(RegisterModel.LocalAddress), model.LocalAddress, "Local Address is required.");
                Required(nameof(RegisterModel.PermanentAddress), model.PermanentAddress, "Permanent Address is required.");
                Required(nameof(RegisterModel.SelectedServices), model.SelectedServices, "Please select at least one service.");
                Required(nameof(RegisterModel.SelectedServiceTypes), model.SelectedServiceTypes, "Please select at least one service type.");
                Required(nameof(RegisterModel.AvailableDays), model.AvailableDays, "Please select at least one available day.");
                Required(nameof(RegisterModel.StartTime), model.StartTime, "Start Time is required.");
                Required(nameof(RegisterModel.EndTime), model.EndTime, "End Time is required.");

                // Email format only; name is intentionally NOT unique.
                if (!string.IsNullOrWhiteSpace(model.Email))
                {
                    try
                    {
                        _ = new MailAddress(model.Email?.Trim() ?? string.Empty);
                    }
                    catch
                    {
                        errors.Add(new
                        {
                            Field = nameof(RegisterModel.Email),
                            Errors = new[] { "Please enter a valid email address." }
                        });
                    }
                }

                // CNIC format.
                const string cnicPattern = @"^\d{5}-\d{7}-\d{1}$";
                if (!string.IsNullOrWhiteSpace(model.CNIC) &&
                    !Regex.IsMatch(model.CNIC.Trim(), cnicPattern))
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.CNIC),
                        Errors = new[] { "Please enter CNIC in XXXXX-XXXXXXX-X format." }
                    });
                }

                // Approved, pending, or blocked CNIC cannot be reused.
                // A rejected registration may reuse its own CNIC during re-registration.
                if (!string.IsNullOrWhiteSpace(model.CNIC))
                {
                    string cnic = model.CNIC.Trim();

                    bool cnicUsed = await _context.Register.AnyAsync(r =>
                        r.CNIC != null &&
                        r.CNIC.Trim() == cnic &&
                        !r.IsRejected &&
                        (!isReRegistration || r.Id != model.Id));

                    if (cnicUsed)
                    {
                        errors.Add(new
                        {
                            Field = nameof(RegisterModel.CNIC),
                            Errors = new[] { "This CNIC is already registered." }
                        });
                    }
                }

                // Service type + conditional address/map validation.
                var serviceTypes = (model.SelectedServiceTypes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (serviceTypes.Length == 0)
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.SelectedServiceTypes),
                        Errors = new[] { "Please select at least one service type." }
                    });
                }

                bool shopSelected = serviceTypes.Any(s =>
                    s.Equals("Shop Service", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("Shop", StringComparison.OrdinalIgnoreCase));

                bool homeSelected = serviceTypes.Any(s =>
                    s.Equals("Home Service", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("Home", StringComparison.OrdinalIgnoreCase));

                if (shopSelected)
                {
                    Required(
                        nameof(RegisterModel.ShopAddressInput),
                        model.ShopAddressInput,
                        "Shop Address is required.");

                    if (!model.ShopLat.HasValue || !model.ShopLng.HasValue)
                    {
                        errors.Add(new
                        {
                            Field = nameof(RegisterModel.ShopLat),
                            Errors = new[] { "Please select the Shop location on the map." }
                        });
                    }
                }

                if (homeSelected)
                {
                    Required(
                        nameof(RegisterModel.HomeAddressInput),
                        model.HomeAddressInput,
                        "Home Address is required.");

                    if (!model.HomeLat.HasValue || !model.HomeLng.HasValue)
                    {
                        errors.Add(new
                        {
                            Field = nameof(RegisterModel.HomeLat),
                            Errors = new[] { "Please select the Home location on the map." }
                        });
                    }
                }

                // Required files for BOTH normal registration and re-registration.
                if (model.ProfileInput == null || model.ProfileInput.Length == 0)
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.ProfileInput),
                        Errors = new[] { "Please upload the profile picture." }
                    });
                }

                if (model.CNICFrontInput == null || model.CNICFrontInput.Length == 0)
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.CNICFrontInput),
                        Errors = new[] { "Please upload the CNIC Front." }
                    });
                }

                if (model.CNICBackInput == null || model.CNICBackInput.Length == 0)
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.CNICBackInput),
                        Errors = new[] { "Please upload the CNIC Back." }
                    });
                }

                if (model.ProviderVideo == null || model.ProviderVideo.Length == 0)
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.ProviderVideo),
                        Errors = new[] { "Please upload the work video." }
                    });
                }

                // Certificate OR Affidavit: one is enough.
                if ((model.Certificate == null || model.Certificate.Length == 0) &&
                    (model.Affidavit == null || model.Affidavit.Length == 0))
                {
                    errors.Add(new
                    {
                        Field = nameof(RegisterModel.Certificate),
                        Errors = new[] { "Please upload either a Certificate or an Affidavit." }
                    });
                }

                if (errors.Count > 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Please correct the highlighted field.",
                        details = errors
                    });
                }

                // Save uploaded files.
                string result = await SaveFile(model.ProfileInput!, true);
                if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, message = result });

                model.ProfilePicPath = result;

                result = await SaveFile(model.CNICFrontInput!, true);
                if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, message = result });

                model.CNICFrontPath = result;

                result = await SaveFile(model.CNICBackInput!, true);
                if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, message = result });

                model.CNICBackPath = result;

                result = await SaveFile(model.ProviderVideo!, true);
                if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, message = result });

                model.VideoPath = result;

                // Certificate and Affidavit are both optional (at least one is required).
                // If the provider uploaded both, save both so the admin can review both.
                model.CertificatePath = null;
                model.AffidavitPath = null;

                if (model.Certificate != null && model.Certificate.Length > 0)
                {
                    result = await SaveFile(model.Certificate, true);
                    if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                        return Json(new { success = false, message = result });

                    model.CertificatePath = result;
                }

                if (model.Affidavit != null && model.Affidavit.Length > 0)
                {
                    result = await SaveFile(model.Affidavit, true);
                    if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                        return Json(new { success = false, message = result });

                    model.AffidavitPath = result;
                }

                if (isReRegistration && existing != null)
                {
                    // UPDATE THE SAME rejected row. No duplicate row is created.
                    existing.Name = model.Name;
                    existing.FatherName = model.FatherName;
                    existing.CNIC = model.CNIC;
                    existing.PhoneNumber = model.PhoneNumber;
                    existing.City = model.City;
                    existing.LocalAddress = model.LocalAddress;
                    existing.PermanentAddress = model.PermanentAddress;
                    existing.SelectedServices = model.SelectedServices;
                    existing.SelectedServiceTypes = model.SelectedServiceTypes ?? string.Empty;
                    existing.AvailableDays = model.AvailableDays;
                    existing.StartTime = model.StartTime;
                    existing.EndTime = model.EndTime;
                    existing.ShopAddressInput = shopSelected ? model.ShopAddressInput : null;
                    existing.ShopLat = shopSelected ? model.ShopLat : null;
                    existing.ShopLng = shopSelected ? model.ShopLng : null;
                    existing.HomeAddressInput = homeSelected ? model.HomeAddressInput : null;
                    existing.HomeLat = homeSelected ? model.HomeLat : null;
                    existing.HomeLng = homeSelected ? model.HomeLng : null;

                    existing.CNICFrontPath = model.CNICFrontPath;
                    existing.CNICBackPath = model.CNICBackPath;
                    existing.ProfilePicPath = model.ProfilePicPath;
                    existing.CertificatePath = model.CertificatePath;
                    existing.AffidavitPath = model.AffidavitPath;
                    existing.VideoPath = model.VideoPath;

                    existing.IsRejected = false;
                    existing.IsApproved = false;
                    existing.IsBlocked = false;
                    existing.RejectionReason = null;
                    existing.Role = "Provider";

                    await _context.SaveChangesAsync();

                    return Json(new
                    {
                        success = true,
                        redirectUrl = Url.Action("RegistrationPending", "Home"),
                        message = "Your re-registration has been submitted for approval."
                    });
                }

                // NORMAL NEW REGISTRATION.
                model.IsRejected = false;
                model.IsApproved = false;
                model.IsBlocked = false;
                model.RejectionReason = null;
                model.Role = "Provider";

                if (!shopSelected)
                {
                    model.ShopAddressInput = null;
                    model.ShopLat = null;
                    model.ShopLng = null;
                }

                if (!homeSelected)
                {
                    model.HomeAddressInput = null;
                    model.HomeLat = null;
                    model.HomeLng = null;
                }

                // The provider identity comes from the verified ProviderSignup row,
                // NOT from the Register table identity. This guarantees that the
                // first verified provider is PRO-1, the second is PRO-2, etc.
                int? verifiedProviderSignupId =
                    HttpContext.Session.GetInt32("VerifiedProviderSignupId");

                if (!verifiedProviderSignupId.HasValue)
                {
                    var sessionRole = HttpContext.Session.GetString("UserRole");
                    if (string.Equals(sessionRole, "Provider", StringComparison.OrdinalIgnoreCase))
                        verifiedProviderSignupId = HttpContext.Session.GetInt32("UserId");
                }

                if (!verifiedProviderSignupId.HasValue || verifiedProviderSignupId.Value <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Your verified provider account could not be found. Please verify your email again."
                    });
                }

                var verifiedProvider = await _context.ProviderSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == verifiedProviderSignupId.Value);

                if (verifiedProvider == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Your provider signup record could not be found. Please verify your email again."
                    });
                }

                model.ProviderId = $"PRO-{verifiedProvider.Id}";
                model.SignupId = verifiedProvider.Id.ToString();
                model.Email = verifiedProvider.Email;

                // The existing SQL Register table requires ExpenseList, while
                // the current registration form does not collect that field.
                // Set it safely if the model exposes it, and also ensure the
                // existing database column has an empty-string default when it
                // is not mapped by EF. Nothing else in the registration flow
                // is changed.
                var expenseProperty = typeof(RegisterModel).GetProperty("ExpenseList");
                if (expenseProperty != null && expenseProperty.CanWrite &&
                    expenseProperty.PropertyType == typeof(string))
                {
                    expenseProperty.SetValue(model, string.Empty);
                }

                var expenseMetadata = _context.Entry(model).Metadata.FindProperty("ExpenseList");
                if (expenseMetadata != null)
                {
                    _context.Entry(model).Property("ExpenseList").CurrentValue = string.Empty;
                }

                await _context.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('dbo.Register', 'ExpenseList') IS NOT NULL
AND NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.Register')
      AND c.name = N'ExpenseList'
)
BEGIN
    ALTER TABLE [dbo].[Register]
    ADD CONSTRAINT [DF_Register_ExpenseList] DEFAULT (N'') FOR [ExpenseList];
END");

                _context.Register.Add(model);
                await _context.SaveChangesAsync();

                return Json(new
                {
                    success = true,
                    redirectUrl = Url.Action("RegistrationPending", "Home"),
                    message = "Registration submitted successfully."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Registration Error: " + ex);
                return Json(new
                {
                    success = false,
                    message = "Registration could not be submitted. Please try again."
                });
            }
        }

        // Updated Safe Helper Method
        private async Task<string> SaveFile(IFormFile file, bool strict = false)
        {
            if (file == null || file.Length == 0) return string.Empty;

            long maxFileSize = file.ContentType.Contains("video") ? 50 * 1024 * 1024 : 5 * 1024 * 1024;
            if (file.Length > maxFileSize)
                return $"ERROR: File {file.FileName} is too large.";

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".mp4", ".avi" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
                return "ERROR: Invalid file type.";

            // Registration uploads only: check the real file content (not just the name),
            // and reject files that are far too small to be a real photo / video.
            if (strict)
            {
                bool isVideoExt = extension == ".mp4" || extension == ".avi";

                if (!isVideoExt && file.Length < 10 * 1024)
                    return $"ERROR: {file.FileName} is too small or low quality. Please upload a clear photo.";

                if (isVideoExt && file.Length < 200 * 1024)
                    return $"ERROR: {file.FileName} is too short or too small to be a valid work video.";

                var header = new byte[12];
                int read;
                using (var headerStream = file.OpenReadStream())
                {
                    read = await headerStream.ReadAsync(header, 0, header.Length);
                }

                bool looksValid;
                if (extension == ".jpg" || extension == ".jpeg")
                    looksValid = read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
                else if (extension == ".png")
                    looksValid = read >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
                else if (extension == ".mp4")
                    looksValid = read >= 8 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70; // "ftyp"
                else // .avi
                    looksValid = read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                              && header[8] == 0x41 && header[9] == 0x56 && header[10] == 0x49; // "RIFF....AVI"

                if (!looksValid)
                    return $"ERROR: {file.FileName} is not a valid {(isVideoExt ? "video" : "image")} file.";

                // Safety net behind the browser checks: reject photos whose real pixel size is tiny.
                // If the size cannot be read, the file is allowed (the browser check and the admin still apply).
                if (!isVideoExt && TryGetImageSize(file, extension, out int imgW, out int imgH))
                {
                    if (Math.Min(imgW, imgH) < 300 || Math.Max(imgW, imgH) < 400)
                        return $"ERROR: {file.FileName} is too small or low quality. Please upload a clear, larger photo.";
                }
            }

            string uploadsFolder = Path.Combine(_environment.WebRootPath, "uploads");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            string uniqueFileName = Guid.NewGuid().ToString() + extension;
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            return "/uploads/" + uniqueFileName;
        }
        // Reads the pixel width/height of a JPG or PNG from its header (no extra library needed).
        private static bool TryGetImageSize(IFormFile file, string extension, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using var stream = file.OpenReadStream();

                if (extension == ".png")
                {
                    var buf = new byte[24];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = stream.Read(buf, got, buf.Length - got);
                        if (n <= 0) return false;
                        got += n;
                    }

                    width = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                    height = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                    return width > 0 && height > 0;
                }

                if (extension == ".jpg" || extension == ".jpeg")
                {
                    // Skip the 0xFFD8 start marker, then walk the segments until the frame header (SOFn).
                    if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return false;

                    var skip = new byte[4096];
                    while (true)
                    {
                        int c = stream.ReadByte();
                        if (c < 0) return false;
                        if (c != 0xFF) continue;

                        int marker = stream.ReadByte();
                        while (marker == 0xFF) marker = stream.ReadByte();
                        if (marker < 0) return false;

                        // Markers without a length field.
                        if (marker == 0x00 || marker == 0x01 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7))
                            continue;

                        // End of image / start of scan reached before any frame header.
                        if (marker == 0xD9 || marker == 0xDA) return false;

                        int hi = stream.ReadByte();
                        int lo = stream.ReadByte();
                        if (lo < 0) return false;
                        int length = (hi << 8) | lo;
                        if (length < 2) return false;

                        bool isFrameHeader = marker >= 0xC0 && marker <= 0xCF
                                             && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                        if (isFrameHeader)
                        {
                            var frame = new byte[5]; // precision, height(2), width(2)
                            int got = 0;
                            while (got < frame.Length)
                            {
                                int n = stream.Read(frame, got, frame.Length - got);
                                if (n <= 0) return false;
                                got += n;
                            }

                            height = (frame[1] << 8) | frame[2];
                            width = (frame[3] << 8) | frame[4];
                            return width > 0 && height > 0;
                        }

                        // Skip the rest of this segment.
                        int remaining = length - 2;
                        while (remaining > 0)
                        {
                            int n = stream.Read(skip, 0, Math.Min(skip.Length, remaining));
                            if (n <= 0) return false;
                            remaining -= n;
                        }
                    }
                }
            }
            catch
            {
                // Unreadable header: let the file through.
            }

            return false;
        }

        private static bool IsStrongPassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;

            // Minimum 6 characters. Longer passwords are allowed.
            return Regex.IsMatch(
                password,
                @"^(?=.{6,}$)(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).+$");
        }

        private static string GetPasswordValidationMessage()
        {
            return "Password must be at least 6 characters and contain at least one uppercase letter, one lowercase letter, one number and one special character.";
        }

        private sealed class SignupVerificationPayload
        {
            public string Role { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public DateTimeOffset ExpiresAtUtc { get; set; }
        }

        private string CreateSignupVerificationToken(string role, string name, string email, string password)
        {
            var payload = new SignupVerificationPayload
            {
                Role = role,
                Name = name.Trim(),
                Email = email.Trim().ToLowerInvariant(),
                Password = password,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30)
            };

            var json = JsonSerializer.Serialize(payload);
            return _signupVerificationProtector.Protect(json);
        }

        private SignupVerificationPayload? ReadSignupVerificationToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            try
            {
                var json = _signupVerificationProtector.Unprotect(token);
                var payload = JsonSerializer.Deserialize<SignupVerificationPayload>(json);

                if (payload == null ||
                    string.IsNullOrWhiteSpace(payload.Email) ||
                    string.IsNullOrWhiteSpace(payload.Password) ||
                    payload.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    return null;
                }

                return payload;
            }
            catch
            {
                return null;
            }
        }

        private async Task SendSignupVerificationEmail(
            string recipientEmail,
            string recipientName,
            string verificationUrl,
            string role)
        {
            if (string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_senderPassword))
                throw new InvalidOperationException("ServiceGo email settings are not configured.");

            using var message = new MailMessage();
            message.From = new MailAddress(_senderEmail, _senderName);
            message.To.Add(new MailAddress(recipientEmail));
            message.ReplyToList.Add(new MailAddress(_senderEmail));
            message.Subject = "ServiceGo - Verify your email address";
            message.IsBodyHtml = true;
            message.Priority = MailPriority.Normal;
            message.Headers.Add("X-Mailer", "ServiceGo");

            var safeName = WebUtility.HtmlEncode(recipientName);
            var safeUrl = WebUtility.HtmlEncode(verificationUrl);
            var accountType = role.Equals("Provider", StringComparison.OrdinalIgnoreCase)
                ? "Provider"
                : "Customer";

            // HTML-only email so Gmail shows the button instead of exposing the long token URL.
            message.Body = $@"
                <!DOCTYPE html>
                <html>
                <body style='margin:0;padding:0;background:#fff7ef;font-family:Arial,sans-serif;color:#333;'>
                    <div style='max-width:600px;margin:0 auto;padding:30px 18px;'>
                        <div style='background:#ffffff;border-radius:16px;padding:30px 24px;text-align:center;border:1px solid #f3dfcf;'>
                            <div style='font-size:28px;font-weight:800;color:#f28c28;margin-bottom:8px;'>ServiceGo</div>
                            <div style='font-size:22px;font-weight:700;color:#333;margin-bottom:22px;'>Verify your email</div>
                            <p style='font-size:16px;line-height:1.6;text-align:left;margin:0 0 12px;'>Hello {safeName},</p>
                            <p style='font-size:16px;line-height:1.6;text-align:left;margin:0 0 10px;'>Thank you for creating your ServiceGo {accountType} account.</p>
                            <p style='font-size:16px;line-height:1.6;text-align:left;margin:0 0 24px;'>Please click the button below to verify your email address.</p>

                            <a href='{safeUrl}'
                               style='display:inline-block;background:#f28c28;color:#ffffff;text-decoration:none;padding:14px 28px;border-radius:10px;font-size:16px;font-weight:700;'>
                                Verify My Email
                            </a>

                            <p style='font-size:13px;line-height:1.5;color:#777;margin:24px 0 6px;'>This verification link expires in 30 minutes.</p>
                            <p style='font-size:13px;line-height:1.5;color:#777;margin:0;'>If you did not create this account, you can safely ignore this email.</p>
                        </div>
                    </div>
                </body>
                </html>";

            using var client = new System.Net.Mail.SmtpClient(_smtpServer, _smtpPort);
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            client.UseDefaultCredentials = false;
            client.EnableSsl = _enableSsl;
            client.Credentials = new NetworkCredential(_senderEmail, _senderPassword);
            client.Timeout = 30000;
            await client.SendMailAsync(message);
        }
        [HttpGet]
        public async Task<IActionResult> VerifySignup(string token)
        {
            var payload = ReadSignupVerificationToken(token);

            if (payload == null)
            {
                TempData["VerificationError"] =
                    "This verification link is invalid or has expired. Please sign up again.";
                return RedirectToAction("SignUp");
            }

            var email = payload.Email.Trim().ToLowerInvariant();

            bool customerExists = await _context.CustomerSignup.AnyAsync(c =>
                c.Email != null && c.Email.Trim().ToLower() == email);

            bool providerExists = await _context.ProviderSignup.AnyAsync(p =>
                p.Email != null && p.Email.Trim().ToLower() == email);

            if (customerExists || providerExists)
            {
                TempData["VerificationError"] =
                    "This email is already verified or already registered. Please login.";
                return RedirectToAction("Login");
            }

            try
            {
                if (payload.Role.Equals("Provider", StringComparison.OrdinalIgnoreCase))
                {
                    var provider = new ProviderSignup
                    {
                        Name = payload.Name,
                        Email = email,
                        Password = HashPassword(payload.Password),
                        Status = "Active"
                    };

                    _context.ProviderSignup.Add(provider);
                    await _context.SaveChangesAsync();

                    HttpContext.Session.SetString("UserEmail", email);
                    HttpContext.Session.SetString("UserRole", "Provider");
                    HttpContext.Session.SetInt32("UserId", provider.Id);
                    HttpContext.Session.SetInt32("VerifiedProviderSignupId", provider.Id);
                    HttpContext.Session.SetString("VerifiedProviderEmail", email);
                    HttpContext.Session.SetString("ProviderId", $"PRO-{provider.Id}");

                    // Keep the requested flow: verification finishes on the
                    // provider home page. Complete Registration on that page
                    // will load this verified provider from Session + DB.
                    return RedirectToAction("Index", "Home");
                }

                var customer = new CustomerSignup
                {
                    Name = payload.Name,
                    Email = email,
                    Password = HashPassword(payload.Password),
                    Status = "Active"
                };

                _context.CustomerSignup.Add(customer);
                await _context.SaveChangesAsync();

                // IMPORTANT: CustomerId is intentionally NOT generated here.
                // It is generated only when the customer completes/saves the profile.
                // This keeps normal signup and booking signup on the same profile flow.

                // Customer ID is generated immediately after email verification.
                // It does not depend on profile completion.
                if (string.IsNullOrWhiteSpace(customer.CustomerId))
                {
                    customer.CustomerId = $"CUS-{customer.Id}";
                    await _context.SaveChangesAsync();
                }

                HttpContext.Session.SetString("UserEmail", email);
                HttpContext.Session.SetString("UserRole", "Customer");
                HttpContext.Session.SetInt32("UserId", customer.Id);
                HttpContext.Session.SetString("UserCode", customer.CustomerId);

                Response.Cookies.Append(
                    "ServiceGoCustomerKnown",
                    "1",
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddYears(1)
                    });

                // If signup started from a booking, create that booking now.
                // Profile completion is only a reminder and is never a booking blocker.
                var pendingBookingJson = HttpContext.Session.GetString("PendingBooking");

                if (!string.IsNullOrWhiteSpace(pendingBookingJson))
                {
                    var createdBookingId = await CreatePendingBookingForCustomerAsync(customer);

                    if (!string.IsNullOrWhiteSpace(createdBookingId))
                        return RedirectToAction("CusDashboard", "Customer");

                    return RedirectToAction("Index", "Home");
                }

                // Normal first-time signup: go to the logged-in Customer Home page.
                return RedirectToAction("Index", "Home");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Signup Verification Error: " + ex);
                TempData["VerificationError"] =
                    "Your email was verified, but the account could not be created right now. Please try again.";
                return RedirectToAction("SignUp");
            }
        }

        [HttpGet]
        public IActionResult ProviderRegistrationStep1()
        {
            ViewBag.VerificationError = TempData["VerificationError"]?.ToString() ?? string.Empty;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProviderRegistrationStep1(
            string Name,
            string Email,
            string Password,
            string ConfirmPassword)
        {
            Name = (Name ?? string.Empty).Trim();
            Email = (Email ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(Name))
                ModelState.AddModelError("Name", "Full Name is required.");

            if (string.IsNullOrWhiteSpace(Email))
                ModelState.AddModelError("Email", "Email is required.");
            else
            {
                try { _ = new MailAddress(Email); }
                catch { ModelState.AddModelError("Email", "Please enter a valid email address."); }
            }

            if (string.IsNullOrWhiteSpace(Password))
                ModelState.AddModelError("Password", "Password is required.");
            else if (!IsStrongPassword(Password))
                ModelState.AddModelError("Password", GetPasswordValidationMessage());

            if (string.IsNullOrWhiteSpace(ConfirmPassword))
                ModelState.AddModelError("ConfirmPassword", "Please confirm your password.");
            else if (Password != ConfirmPassword)
                ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");

            _context.ChangeTracker.Clear();

            var existingProvider = await _context.ProviderSignup
                .FirstOrDefaultAsync(p =>
                    p.Email != null &&
                    p.Email.Trim().ToLower() == Email.ToLowerInvariant());

            var existingCustomer = await _context.CustomerSignup
                .FirstOrDefaultAsync(c =>
                    c.Email != null &&
                    c.Email.Trim().ToLower() == Email.ToLowerInvariant());

            if (existingProvider?.Status == "Blocked" || existingCustomer?.Status == "Blocked")
                ModelState.AddModelError("Email", "This email belongs to a blocked account. Please contact support.");
            else if (existingProvider != null || existingCustomer != null)
                ModelState.AddModelError("Email", "Email is already used.");

            if (!ModelState.IsValid)
            {
                ViewData["SignupName"] = Name;
                ViewData["SignupEmail"] = Email;
                return View();
            }

            try
            {
                var token = CreateSignupVerificationToken("Provider", Name, Email, Password);

                var verificationUrl = Url.Action(
                    "VerifySignup",
                    "Home",
                    new { token },
                    Request.Scheme);

                await SendSignupVerificationEmail(
                    Email,
                    Name,
                    verificationUrl ?? $"{Request.Scheme}://{Request.Host}/Home/VerifySignup?token={WebUtility.UrlEncode(token)}",
                    "Provider");

                ViewBag.VerificationEmail = Email;
                ViewBag.ShowVerificationPrompt = true;
                ViewBag.VerificationError = string.Empty;
                ViewData["SignupName"] = Name;
                ViewData["SignupEmail"] = Email;
                ModelState.Clear();
                return View("ProviderRegistrationStep1");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Provider Signup Email Error: " + ex);
                ModelState.AddModelError("", "We could not send the verification email right now. Please try again.");
                ViewData["SignupName"] = Name;
                ViewData["SignupEmail"] = Email;
                return View();
            }
        }

        [HttpGet]
        public IActionResult SignUp()
        {
            ViewBag.VerificationError = TempData["VerificationError"]?.ToString() ?? string.Empty;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SignUp(
            string Name,
            string Email,
            string Password,
            string ConfirmPassword)
        {
            Name = (Name ?? string.Empty).Trim();
            Email = (Email ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(Name))
                ModelState.AddModelError("Name", "Full Name is required.");

            if (string.IsNullOrWhiteSpace(Email))
                ModelState.AddModelError("Email", "Email is required.");
            else
            {
                try { _ = new MailAddress(Email); }
                catch { ModelState.AddModelError("Email", "Please enter a valid email address."); }
            }

            if (string.IsNullOrWhiteSpace(Password))
                ModelState.AddModelError("Password", "Password is required.");
            else if (!IsStrongPassword(Password))
                ModelState.AddModelError("Password", GetPasswordValidationMessage());

            if (string.IsNullOrWhiteSpace(ConfirmPassword))
                ModelState.AddModelError("ConfirmPassword", "Please confirm your password.");
            else if (Password != ConfirmPassword)
                ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");

            var existingCustomer = await _context.CustomerSignup
                .FirstOrDefaultAsync(c =>
                    c.Email != null &&
                    c.Email.Trim().ToLower() == Email.ToLowerInvariant());

            var existingProvider = await _context.ProviderSignup
                .FirstOrDefaultAsync(p =>
                    p.Email != null &&
                    p.Email.Trim().ToLower() == Email.ToLowerInvariant());

            if (existingCustomer?.Status == "Blocked" || existingProvider?.Status == "Blocked")
                ModelState.AddModelError("Email", "This email belongs to a blocked account. Please contact support.");
            else if (existingCustomer != null || existingProvider != null)
                ModelState.AddModelError("Email", "Email is already used.");

            if (!ModelState.IsValid)
            {
                return View(new CustomerSignup
                {
                    Name = Name,
                    Email = Email
                });
            }

            try
            {
                var token = CreateSignupVerificationToken("Customer", Name, Email, Password);

                var verificationUrl = Url.Action(
                    "VerifySignup",
                    "Home",
                    new { token },
                    Request.Scheme);

                await SendSignupVerificationEmail(
                    Email,
                    Name,
                    verificationUrl ?? $"{Request.Scheme}://{Request.Host}/Home/VerifySignup?token={WebUtility.UrlEncode(token)}",
                    "Customer");

                ViewBag.VerificationEmail = Email;
                ViewBag.ShowVerificationPrompt = true;
                ViewBag.VerificationError = string.Empty;
                ModelState.Clear();
                return View(new CustomerSignup
                {
                    Name = Name,
                    Email = Email
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Customer Signup Email Error: " + ex);
                ModelState.AddModelError("", "We could not send the verification email right now. Please try again.");
                return View(new CustomerSignup
                {
                    Name = Name,
                    Email = Email
                });
            }
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View(new Login());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(Login model)
        {
            if (!ModelState.IsValid)
                return View(model);

            string email = (model.Email ?? string.Empty).Trim();
            string password = model.Password ?? string.Empty;

            // 1. ADMIN LOGIN
            var admin = await _context.Admins
                .FirstOrDefaultAsync(a =>
                    a.Email != null &&
                    a.Email.Trim().ToLower() == email.ToLower());

            if (admin != null && VerifyPassword(admin.Password, password, out bool adminUpgrade))
            {
                if (adminUpgrade)
                {
                    admin.Password = HashPassword(password);
                    await _context.SaveChangesAsync();
                }

                HttpContext.Session.SetString("UserEmail", admin.Email ?? email);
                HttpContext.Session.SetString("UserRole", "Admin");
                HttpContext.Session.SetInt32("UserId", admin.Id);

                return RedirectToAction("Dashboard", "Admin");
            }

            // 2. CUSTOMER LOGIN
            // Successful customer login always goes to the Customer Home page.
            var customer = await _context.CustomerSignup
                .FirstOrDefaultAsync(c =>
                    c.Email != null &&
                    c.Email.Trim().ToLower() == email.ToLower());

            if (customer != null && VerifyPassword(customer.Password, password, out bool customerUpgrade))
            {
                if (customerUpgrade)
                {
                    customer.Password = HashPassword(password);
                    await _context.SaveChangesAsync();
                }

                if (string.Equals(customer.Status, "Blocked", StringComparison.OrdinalIgnoreCase))
                {
                    ViewBag.ShowBlockedAccount = true;
                    HttpContext.Session.Clear();
                    return View(model);
                }

                HttpContext.Session.SetString("UserEmail", customer.Email ?? email);
                HttpContext.Session.SetString("UserRole", "Customer");
                HttpContext.Session.SetInt32("UserId", customer.Id);
                HttpContext.Session.SetString(
                    "UserCode",
                    customer.CustomerId ?? $"CUS-{customer.Id}");

                Response.Cookies.Append(
                    "ServiceGoCustomerKnown",
                    "1",
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddYears(1)
                    });

                // If login was opened during a booking flow, finish the saved
                // booking immediately. Profile completion is NOT required for booking.
                var pendingBookingJson = HttpContext.Session.GetString("PendingBooking");

                if (!string.IsNullOrWhiteSpace(pendingBookingJson))
                {
                    var createdBookingId = await CreatePendingBookingForCustomerAsync(customer);

                    if (!string.IsNullOrWhiteSpace(createdBookingId))
                        return RedirectToAction("CusDashboard", "Customer");

                    return RedirectToAction("Index", "Home");
                }

                return RedirectToAction("Index", "Home");
            }

            // 3. PROVIDER LOGIN
            // Successful provider login always goes to the Provider Home page.
            // Registration/approval/rejection/incomplete status is handled on Home.
            var providerSignup = await _context.ProviderSignup
                .FirstOrDefaultAsync(p =>
                    p.Email != null &&
                    p.Email.Trim().ToLower() == email.ToLower());

            if (providerSignup != null && VerifyPassword(providerSignup.Password, password, out bool providerUpgrade))
            {
                if (providerUpgrade)
                {
                    providerSignup.Password = HashPassword(password);
                    await _context.SaveChangesAsync();
                }

                var providerProfile = await _context.Register
                    .Where(r =>
                        r.Email != null &&
                        r.Email.Trim().ToLower() == email.ToLower())
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync();

                bool providerBlocked =
                    string.Equals(providerSignup.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ||
                    (providerProfile?.IsBlocked ?? false);

                if (providerBlocked)
                {
                    ViewBag.ShowBlockedAccount = true;
                    HttpContext.Session.Clear();
                    return View(model);
                }

                HttpContext.Session.SetString("UserEmail", providerSignup.Email ?? email);
                HttpContext.Session.SetString("UserRole", "Provider");
                HttpContext.Session.SetInt32("UserId", providerProfile?.Id ?? providerSignup.Id);

                return RedirectToAction("Index", "Home");
            }

            ModelState.AddModelError("Password", "Invalid Email or Password.");
            return View(model);
        }
        private string GenerateOtp()
        {
            return RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        }

        private void SendOtpEmail(string recipientEmail, string otp, string purpose)
        {
            if (string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_senderPassword))
                throw new InvalidOperationException("ServiceGo email settings are not configured.");

            using var message = new MailMessage();
            message.From = new MailAddress(_senderEmail, _senderName);
            message.To.Add(new MailAddress(recipientEmail));
            message.ReplyToList.Add(new MailAddress(_senderEmail));
            message.Subject = $"ServiceGo - {purpose} OTP";
            message.IsBodyHtml = true;
            message.Body = $@"
                <div style='font-family:Arial,sans-serif;max-width:600px;margin:auto;'>
                    <h2 style='color:#f0ad4e;'>ServiceGo</h2>
                    <p>Your password reset OTP is:</p>
                    <h1 style='letter-spacing:8px;'>{otp}</h1>
                    <p>This OTP will expire in <strong>5 minutes</strong>.</p>
                    <p>If you did not request this code, please ignore this email.</p>
                </div>";

            using var client = new System.Net.Mail.SmtpClient(_smtpServer, _smtpPort);
            client.EnableSsl = _enableSsl;
            client.Credentials = new NetworkCredential(_senderEmail, _senderPassword);
            client.Send(message);
        }

        [HttpGet]
        public IActionResult Forget()
        {
            return View(new ForgetModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Forget(ForgetModel model)
        {
            // Normalize the bound value before validation so leading/trailing spaces
            // do not make a valid email look invalid.
            model.Email = model.Email?.Trim();

            if (string.IsNullOrWhiteSpace(model.Email))
            {
                return Json(new
                {
                    success = false,
                    message = "Email address is required.",
                    errors = new[]
                    {
                        new { Field = nameof(ForgetModel.Email), Errors = new[] { "Email address is required." } }
                    }
                });
            }

            try
            {
                _ = new MailAddress(model.Email);
            }
            catch
            {
                return Json(new
                {
                    success = false,
                    message = "Please enter a valid email address.",
                    errors = new[]
                    {
                        new { Field = nameof(ForgetModel.Email), Errors = new[] { "Please enter a valid email address." } }
                    }
                });
            }

            string email = model.Email.ToLowerInvariant();

            bool customerExists = await _context.CustomerSignup.AnyAsync(c => c.Email != null && c.Email.Trim().ToLower() == email);
            bool providerExists = await _context.ProviderSignup.AnyAsync(p => p.Email != null && p.Email.Trim().ToLower() == email);

            if (!customerExists && !providerExists)
                return Json(new { success = false, message = "No account was found with this email address." });

            string otp = GenerateOtp();
            HttpContext.Session.SetString("ResetEmail", email);
            HttpContext.Session.SetString("ResetOtp", otp);
            HttpContext.Session.SetString("ResetOtpExpiry", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            HttpContext.Session.Remove("PasswordResetVerified");

            try
            {
                SendOtpEmail(email, otp, "Password Reset");
                return Json(new
                {
                    success = true,
                    redirectUrl = Url.Action("VerifyOtp", "Home"),
                    message = "OTP sent successfully."
                });
            }
            catch (Exception ex)
            {
                HttpContext.Session.Remove("ResetEmail");
                HttpContext.Session.Remove("ResetOtp");
                HttpContext.Session.Remove("ResetOtpExpiry");
                Console.WriteLine("ServiceGo OTP Email Error: " + ex);
                return Json(new { success = false, message = "OTP email could not be sent right now. Please check your email settings." });
            }
        }

        private object GetModelErrors()
        {
            return ModelState.Where(x => x.Value?.Errors.Count > 0)
                .Select(x => new { Field = x.Key, Errors = x.Value!.Errors.Select(e => e.ErrorMessage).ToArray() })
                .ToArray();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ResendOtp()
        {
            string? email = HttpContext.Session.GetString("ResetEmail");
            if (string.IsNullOrWhiteSpace(email))
                return Json(new { success = false, message = "OTP session expired. Please request a new OTP." });

            string otp = GenerateOtp();
            DateTimeOffset otpExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            HttpContext.Session.SetString("ResetOtp", otp);
            HttpContext.Session.SetString("ResetOtpExpiry", otpExpiry.ToString("O"));
            HttpContext.Session.Remove("PasswordResetVerified");

            try
            {
                SendOtpEmail(email, otp, "Password Reset");
                return Json(new
                {
                    success = true,
                    message = "A new OTP has been sent to your email.",
                    expiresAtUtc = otpExpiry.ToString("O")
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo OTP Resend Error: " + ex);
                return Json(new { success = false, message = "OTP email could not be sent right now. Please check the email settings." });
            }
        }

        [HttpGet]
        public IActionResult VerifyOtp()
        {
            string? email = HttpContext.Session.GetString("ResetEmail");
            string? expiryString = HttpContext.Session.GetString("ResetOtpExpiry");

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(expiryString))
                return RedirectToAction("Forget");

            // The browser countdown is only a visual aid. The server-side expiry
            // remains authoritative when VerifyOtp is submitted.
            ViewBag.OtpExpiryUtc = expiryString;
            return View(new VerifyOtpModel { Email = email });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult VerifyOtp(string? Otp, string? Email)
        {
            // The OTP page uses six visual boxes and copies their value into
            // the hidden Otp field. Validate that final value directly instead
            // of letting VerifyOtp model validation reject it first.
            Otp = (Otp ?? string.Empty).Trim();
            Email = (Email ?? string.Empty).Trim();

            if (!Regex.IsMatch(Otp, @"^\d{6}$"))
            {
                return Json(new
                {
                    success = false,
                    message = "Please enter the 6-digit OTP."
                });
            }

            string? savedEmail = HttpContext.Session.GetString("ResetEmail");
            string? savedOtp = HttpContext.Session.GetString("ResetOtp");
            string? expiryString = HttpContext.Session.GetString("ResetOtpExpiry");

            if (string.IsNullOrWhiteSpace(savedEmail) ||
                string.IsNullOrWhiteSpace(savedOtp) ||
                string.IsNullOrWhiteSpace(expiryString))
            {
                return Json(new
                {
                    success = false,
                    message = "OTP session expired. Please request a new OTP."
                });
            }

            if (!string.IsNullOrWhiteSpace(Email) &&
                !string.Equals(Email, savedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Json(new
                {
                    success = false,
                    message = "OTP email does not match the current reset session. Please request a new OTP."
                });
            }

            if (!DateTimeOffset.TryParse(
                    expiryString,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset expiry))
            {
                return Json(new
                {
                    success = false,
                    message = "Invalid OTP session. Please request a new OTP."
                });
            }

            if (DateTimeOffset.UtcNow > expiry)
            {
                HttpContext.Session.Remove("ResetOtp");
                HttpContext.Session.Remove("ResetOtpExpiry");
                HttpContext.Session.Remove("PasswordResetVerified");

                return Json(new
                {
                    success = false,
                    message = "OTP has expired. Please request a new OTP."
                });
            }

            if (!string.Equals(Otp, savedOtp.Trim(), StringComparison.Ordinal))
            {
                return Json(new
                {
                    success = false,
                    message = "Invalid OTP. Please enter the 6-digit code sent to your email."
                });
            }

            // Correct OTP: allow the ResetPassword page.
            HttpContext.Session.SetString("PasswordResetVerified", "true");

            // OTP is one-time use.
            HttpContext.Session.Remove("ResetOtp");
            HttpContext.Session.Remove("ResetOtpExpiry");

            return Json(new
            {
                success = true,
                redirectUrl = Url.Action("ResetPassword", "Home"),
                message = "OTP verified successfully."
            });
        }

        [HttpGet]
        public IActionResult ResetPassword()
        {
            string? verified = HttpContext.Session.GetString("PasswordResetVerified");
            string? email = HttpContext.Session.GetString("ResetEmail");

            if (verified != "true" || string.IsNullOrWhiteSpace(email))
                return RedirectToAction("Forget");

            return View(new ResetPasswordModel { Email = email });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordModel model)
        {
            string? verified = HttpContext.Session.GetString("PasswordResetVerified");
            string? sessionEmail = HttpContext.Session.GetString("ResetEmail");

            bool isAjax = string.Equals(
                Request.Headers["X-Requested-With"].ToString(),
                "XMLHttpRequest",
                StringComparison.OrdinalIgnoreCase);

            if (verified != "true" || string.IsNullOrWhiteSpace(sessionEmail))
            {
                if (isAjax)
                    return Json(new
                    {
                        success = false,
                        message = "Your password reset session has expired. Please request a new OTP.",
                        redirectUrl = Url.Action("Forget", "Home")
                    });

                return RedirectToAction("Forget");
            }

            model.Email = sessionEmail.Trim();

            // Email is trusted from the verified reset session.
            ModelState.Remove(nameof(ResetPasswordModel.Email));

            // Ignore any old maximum-length annotation on the password model.
            // Current rule: minimum 6 characters; longer passwords are allowed.
            ModelState.Remove(nameof(ResetPasswordModel.NewPassword));
            ModelState.Remove(nameof(ResetPasswordModel.ConfirmPassword));

            if (!IsStrongPassword(model.NewPassword))
            {
                ModelState.AddModelError(
                    nameof(ResetPasswordModel.NewPassword),
                    "Password must be at least 6 characters and contain at least one uppercase letter, one lowercase letter, one number and one special character.");
            }

            if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(ResetPasswordModel.ConfirmPassword),
                    "Passwords do not match.");
            }

            if (!ModelState.IsValid)
            {
                if (isAjax)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Please correct the password fields.",
                        errors = GetModelErrors()
                    });
                }

                return View(model);
            }

            string email = sessionEmail.Trim().ToLowerInvariant();

            var customer = await _context.CustomerSignup
                .FirstOrDefaultAsync(c =>
                    c.Email != null &&
                    c.Email != null && c.Email.Trim().ToLower() == email);

            if (customer != null)
            {
                customer.Password = HashPassword(model.NewPassword);
            }
            else
            {
                var provider = await _context.ProviderSignup
                    .FirstOrDefaultAsync(p =>
                        p.Email != null &&
                        p.Email != null && p.Email.Trim().ToLower() == email);

                if (provider == null)
                {
                    if (isAjax)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Account could not be found. Please start the password reset process again."
                        });
                    }

                    ModelState.AddModelError(
                        nameof(ResetPasswordModel.Email),
                        "Account could not be found.");
                    return View(model);
                }

                provider.Password = HashPassword(model.NewPassword);
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Password Reset Save Error: " + ex);

                if (isAjax)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Password could not be updated right now. Please try again."
                    });
                }

                ModelState.AddModelError(
                    "",
                    "Password could not be updated right now. Please try again.");
                return View(model);
            }

            // Make the verified OTP/session one-time only after the password
            // has actually been saved successfully.
            HttpContext.Session.Remove("PasswordResetVerified");
            HttpContext.Session.Remove("ResetEmail");
            HttpContext.Session.Remove("ResetOtp");
            HttpContext.Session.Remove("ResetOtpExpiry");

            const string successMessage =
                "Your password has been updated successfully. Please login with your new password.";

            if (isAjax)
            {
                return Json(new
                {
                    success = true,
                    message = successMessage,
                    redirectUrl = Url.Action("Login", "Home")
                });
            }

            TempData["PasswordResetSuccess"] = successMessage;
            return RedirectToAction("Login");
        }


        [HttpGet]
        public async Task<IActionResult> ReRegistration(int? id = null, string? email = null)
        {
            RegisterModel? registration = null;

            if (id.HasValue && id.Value > 0)
            {
                registration = await _context.Register
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == id.Value && r.IsRejected);
            }

            if (registration == null && !string.IsNullOrWhiteSpace(email))
            {
                string normalizedEmail = email.Trim().ToLowerInvariant();
                registration = await _context.Register
                    .AsNoTracking()
                    .Where(r => r.Email != null && r.IsRejected)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync(r => r.Email!.Trim().ToLower() == normalizedEmail);
            }

            if (registration == null)
            {
                string? sessionEmail = HttpContext.Session.GetString("UserEmail");
                if (!string.IsNullOrWhiteSpace(sessionEmail))
                {
                    string normalizedEmail = sessionEmail.Trim().ToLowerInvariant();
                    registration = await _context.Register
                        .AsNoTracking()
                        .Where(r => r.Email != null && r.IsRejected)
                        .OrderByDescending(r => r.Id)
                        .FirstOrDefaultAsync(r => r.Email!.Trim().ToLower() == normalizedEmail);
                }
            }

            if (registration == null)
            {
                TempData["ReRegistrationError"] = "Rejected provider registration record could not be found.";
                return RedirectToAction("Login");
            }

            ViewBag.IsReRegistration = true;
            ViewBag.RejectionReason = registration.RejectionReason;
            ViewBag.ProviderId = registration.ProviderId;
            ViewBag.Email = registration.Email;

            // Return the complete rejected record so the re-registration form
            // is pre-filled with the user's existing information. The same Id
            // is posted back and the Register action updates that exact row.
            return View("Register", registration);
        }

        [HttpGet]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult LogoutConfirmed()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Index()
        {
            string? email = HttpContext.Session.GetString("UserEmail");
            string? role = HttpContext.Session.GetString("UserRole");

            email = email?.Trim();
            role = role?.Trim();

            bool isLoggedIn = !string.IsNullOrWhiteSpace(email) &&
                              !string.IsNullOrWhiteSpace(role);

            if (isLoggedIn &&
                role!.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            {
                var currentCustomer = await _context.CustomerSignup
                    .AsNoTracking()
                    .Where(c => c.Email != null &&
                                c.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(c => c.Id)
                    .FirstOrDefaultAsync();

                if (currentCustomer != null &&
                    string.Equals(currentCustomer.Status, "Blocked", StringComparison.OrdinalIgnoreCase))
                {
                    HttpContext.Session.Clear();
                    isLoggedIn = false;
                    role = string.Empty;
                    email = null;
                }
            }
            else if (isLoggedIn &&
                     role!.Equals("Provider", StringComparison.OrdinalIgnoreCase))
            {
                var currentProviderSignup = await _context.ProviderSignup
                    .AsNoTracking()
                    .Where(p => p.Email != null &&
                                p.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(p => p.Id)
                    .FirstOrDefaultAsync();

                var currentProviderRegistration = await _context.Register
                    .AsNoTracking()
                    .Where(r => r.Email != null &&
                                r.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync();

                bool currentProviderBlocked =
                    string.Equals(currentProviderSignup?.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ||
                    (currentProviderRegistration?.IsBlocked ?? false);

                if (currentProviderBlocked)
                {
                    HttpContext.Session.Clear();
                    isLoggedIn = false;
                    role = string.Empty;
                    email = null;
                }
            }

            ViewBag.IsLoggedIn = isLoggedIn;
            ViewBag.ShowBlockedAccount = false;
            ViewBag.UserRole = role ?? string.Empty;
            ViewBag.ProviderState = string.Empty;
            ViewBag.ProviderName = string.Empty;
            ViewBag.ProviderRejectionReason = string.Empty;
            ViewBag.CustomerName = string.Empty;

            if (isLoggedIn &&
                role!.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            {
                var customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .Where(c => c.Email != null &&
                                c.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(c => c.Id)
                    .FirstOrDefaultAsync();

                ViewBag.CustomerName = customer?.Name ?? string.Empty;

                // Keep the existing Home page profile-completion reminder active.
                // Profile completion is based only on Phone, City and Address.
                ViewBag.CustomerProfileIncomplete = customer != null &&
                    (string.IsNullOrWhiteSpace(customer.Phone) ||
                     string.IsNullOrWhiteSpace(customer.City) ||
                     string.IsNullOrWhiteSpace(customer.Address));
            }
            else
            {
                ViewBag.CustomerProfileIncomplete = false;
            }

            if (isLoggedIn &&
                role!.Equals("Provider", StringComparison.OrdinalIgnoreCase))
            {
                var registration = await _context.Register
                    .AsNoTracking()
                    .Where(r => r.Email != null &&
                                r.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync();

                var providerSignup = await _context.ProviderSignup
                    .AsNoTracking()
                    .Where(p => p.Email != null &&
                                p.Email.Trim().ToLower() == email!.Trim().ToLower())
                    .OrderByDescending(p => p.Id)
                    .FirstOrDefaultAsync();

                // Provider ka naam signup se available rehta hai even before
                // the full registration form is completed. Once registration
                // exists, prefer the registration name.
                // Always prefer the actual provider name from the registration/profile record.
                // If the full registration has not been created yet, use the verified signup record.
                string providerName = registration?.Name ?? providerSignup?.Name ?? string.Empty;

                // Final safe fallback for an already-authenticated provider: use the session value
                // if an older login flow stored the display name there.
                if (string.IsNullOrWhiteSpace(providerName))
                {
                    providerName = HttpContext.Session.GetString("UserName")?.Trim() ?? string.Empty;
                }

                ViewBag.ProviderName = providerName;

                if (registration == null)
                {
                    ViewBag.ProviderState = "Incomplete";
                }
                else if (registration.IsRejected)
                {
                    ViewBag.ProviderState = "Rejected";
                    ViewBag.ProviderRejectionReason = string.IsNullOrWhiteSpace(registration.RejectionReason)
                        ? "Your registration did not meet the required criteria"
                        : registration.RejectionReason;
                }
                else if (registration.IsApproved)
                {
                    ViewBag.ProviderState = "Approved";
                }
                else
                {
                    ViewBag.ProviderState = "Pending";
                }
            }

            return View();
        }
        public IActionResult Choserole()
        {
            return View();
        }
        public IActionResult SearchResult(string city, string service)
        {
            // Only approved and non-blocked providers are searchable.
            var providers = _context.Register.AsNoTracking()
                .Where(p => p.IsApproved && !p.IsBlocked)
                .AsQueryable();

            city = city?.Trim();
            service = service?.Trim();

            bool cityExists = true;

            if (!string.IsNullOrWhiteSpace(city))
            {
                // Keep the comparison EF Core can translate to SQL.
                cityExists = _context.Register.AsNoTracking()
                    .Any(p => p.IsApproved &&
                              !p.IsBlocked &&
                              p.City != null &&
                              p.City.Trim().ToLower() == city.ToLower());

                providers = providers.Where(p =>
                    p.City != null &&
                    p.City.Trim().ToLower() == city.ToLower());
            }

            // City list is populated from all approved, non-blocked providers
            // so every registered provider city remains available in the dropdown.
            ViewBag.ProviderCities = _context.Register.AsNoTracking()
                .Where(p => p.IsApproved && !p.IsBlocked && p.City != null)
                .Select(p => p.City!.Trim())
                .Where(c => c != null && c != string.Empty)
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            if (!string.IsNullOrWhiteSpace(service))
            {
                var normalizedService = service.Trim();
                providers = providers.AsEnumerable()
                    .Where(p => (p.SelectedServices ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(s => string.Equals(s, normalizedService, StringComparison.OrdinalIgnoreCase)))
                    .AsQueryable();
            }

            var results = providers.ToList();

            if (!string.IsNullOrWhiteSpace(city) && !cityExists)
            {
                TempData["SearchMessage"] = $"Invalid city: {city}. Please enter a valid city.";
            }

            return View(results);
        }


        private static string BuildWorkingHours(string? startTime, string? endTime)
        {
            string FormatTime(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;

                var trimmed = value.Trim();
                var formats = new[]
                {
                    "H:mm", "HH:mm", "h:mm", "hh:mm",
                    "H:mm:ss", "HH:mm:ss", "h:mm:ss", "hh:mm:ss",
                    "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt"
                };

                if (DateTime.TryParseExact(
                        trimmed,
                        formats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out var parsed))
                {
                    return parsed.ToString("hh:mm tt", CultureInfo.InvariantCulture);
                }

                if (DateTime.TryParse(
                        trimmed,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out parsed))
                {
                    return parsed.ToString("hh:mm tt", CultureInfo.InvariantCulture);
                }

                return trimmed;
            }

            var start = FormatTime(startTime);
            var end = FormatTime(endTime);

            if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(start))
                return end;

            if (string.IsNullOrWhiteSpace(end))
                return start;

            return $"{start} to {end}";
        }

        public IActionResult ViewProfile(int id)
        {
            // Database se data fetch karein
            var provider = _context.Register.FirstOrDefault(p => p.Id == id);

            // Agar provider database mein nahi mila to Home page par redirect karein
            if (provider == null || provider.IsBlocked)
            {
                return RedirectToAction("Index", "Home");
            }

            // Provider ke completed bookings se actual customer reviews
            // nikale ja rahe hain. Top 5 highest-rated reviews ViewProfile par show honge.
            var providerReviews = _context.Booking
                .AsNoTracking()
                .Where(b =>
                    b.ProviderId == provider.Id &&
                    b.Rating.HasValue)
                .OrderByDescending(b => b.Rating)
                .ThenByDescending(b => b.Id)
                .Take(5)
                .Select(b => new
                {
                    CustomerName = b.CustomerName,
                    Rating = b.Rating!.Value,
                    Comment = b.ReviewComment
                })
                .ToList();

            // ViewProfile model mein data map karein
            var viewModel = new Viewprofile
            {
                ProviderId = provider.Id,
                FullName = provider.Name,
                ProfileImage = provider.ProfilePicPath,
                ProviderCode = provider.ProviderId,
                Rating = provider.Rating,
                ReviewsCount = provider.ReviewsCount,
                Status = provider.Status,
                ServiceCategory = provider.SelectedServices,
                ServiceType = provider.SelectedServiceTypes,

                // Provider registration se working days aur hours
                WorkingDays = provider.AvailableDays,
                AvailableHours = BuildWorkingHours(provider.StartTime, provider.EndTime),

                CityHub = provider.City,
                ContactNumber = provider.PhoneNumber,

                // Locations mapping
                ShopAddress = provider.ShopAddressInput,
                ShopLat = provider.ShopLat?.ToString(),
                ShopLng = provider.ShopLng?.ToString(),

                HomeAddress = provider.HomeAddressInput,
                HomeLat = provider.HomeLat?.ToString(),
                HomeLng = provider.HomeLng?.ToString()
            };

            ViewBag.ProviderReviews = providerReviews;

            return View(viewModel);
        }

        [HttpGet]
        public IActionResult Booking(int id)
        {
            // Provider ka data fetch karein taake check kar sakein ke wo exist karta hai ya nahi
            var provider = _context.Register.FirstOrDefault(p => p.Id == id);

            if (provider == null || provider.IsBlocked)
            {
                return RedirectToAction("Index", "Home");
            }

            // View Profile public hai. Authentication sirf Book Now ke baad required hai.
            var userRole = HttpContext.Session.GetString("UserRole")?.Trim();
            var userEmail = HttpContext.Session.GetString("UserEmail")?.Trim();
            var userId = HttpContext.Session.GetInt32("UserId");

            bool isCustomerLoggedIn =
                string.Equals(userRole, "Customer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(userEmail) &&
                userId.HasValue &&
                userId.Value > 0;

            // Book Now always opens the booking form first. Authentication is
            // required only when the customer presses Confirm Booking.
            // Remember the provider now so the form/login/signup flow never loses it.
            HttpContext.Session.SetInt32("PendingBookingProviderId", id);

            var bookingCustomer = isCustomerLoggedIn
                ? _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefault(c => c.Id == userId!.Value)
                : null;

            ViewBag.CustomerName = bookingCustomer?.Name ?? string.Empty;
            ViewBag.CustomerEmail = bookingCustomer?.Email ?? string.Empty;

            // 1. Provider ki services ko comma (,) se split kar ke list bana rahe hain dropdown ke liye
            var servicesList = new List<string>();
            if (!string.IsNullOrEmpty(provider.SelectedServices))
            {
                servicesList = provider.SelectedServices.Split(',').Select(s => s.Trim()).ToList();
            }

            // 2. Provider ki service types ko comma (,) se split kar ke list bana rahe hain
            var serviceTypesList = new List<string>();
            if (!string.IsNullOrEmpty(provider.SelectedServiceTypes))
            {
                serviceTypesList = provider.SelectedServiceTypes.Split(',').Select(s => s.Trim()).ToList();
            }

            // Dropdown aur view ke liye ViewBag mein direct values set kar rahe hain (SelectList hata diya hai)
            ViewBag.ProviderServices = servicesList;
            ViewBag.ProviderServiceTypes = serviceTypesList;

            ViewBag.ProviderId = provider.Id;
            ViewBag.ProviderName = provider.Name;
            ViewBag.ProviderAvailableDays = NormalizeProviderDaysForBooking(provider.AvailableDays);

            var bookingProviderStart = ParseProviderTimeForBooking(provider.StartTime);
            var bookingProviderEnd = ParseProviderTimeForBooking(provider.EndTime);

            // Send normalized 24-hour values to the booking page so the browser
            // can automatically set the provider's exact selectable time range.
            ViewBag.ProviderStartTime = bookingProviderStart?.ToString("HH:mm") ?? string.Empty;
            ViewBag.ProviderEndTime = bookingProviderEnd?.ToString("HH:mm") ?? string.Empty;

            // Map aur addresses ke liye exact model properties pass ki ja rahi hain
            ViewBag.ShopLat = provider.ShopLat ?? 0;
            ViewBag.ShopLng = provider.ShopLng ?? 0;
            ViewBag.HomeLat = provider.HomeLat ?? 0;
            ViewBag.HomeLng = provider.HomeLng ?? 0;
            ViewBag.ShopAddress = provider.ShopAddressInput;
            ViewBag.HomeAddress = provider.HomeAddressInput;

            // View ko 'Booking' model chahiye isliye naya Booking object pass kar rahe hain
            var bookingModel = new Booking();

            return View(bookingModel);
        }

        private static string NormalizeProviderDaysForBooking(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var fullDays = new[]
            {
                "sunday", "monday", "tuesday", "wednesday",
                "thursday", "friday", "saturday"
            };

            var shortDays = new[]
            {
                "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"
            };

            var normalized = value
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Replace("\"", string.Empty)
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(day => day.Trim().ToLowerInvariant())
                .Select(day =>
                {
                    for (int i = 0; i < fullDays.Length; i++)
                    {
                        if (day == fullDays[i] || day == fullDays[i].Substring(0, 3))
                            return shortDays[i];
                    }

                    return day.ToUpperInvariant();
                })
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(",", normalized);
        }

        private static DateTime? ParseProviderTimeForBooking(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string[] formats = { "H:mm", "HH:mm", "h:mm", "hh:mm", "h:mm tt", "hh:mm tt", "H:mm:ss", "HH:mm:ss", "h:mm:ss tt", "hh:mm:ss tt" };
            if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return parsed;

            if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                return parsed;

            return null;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitBooking(Booking model, IFormFile? ProblemImage)
        {
            try
            {
                var sessionRole = HttpContext.Session.GetString("UserRole")?.Trim();
                var sessionEmail = HttpContext.Session.GetString("UserEmail")?.Trim();
                var sessionUserId = HttpContext.Session.GetInt32("UserId");

                // ---------------------------------------------------------
                // 1. Validate provider first.
                // ---------------------------------------------------------
                if (model.ProviderId <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "A valid provider is required for this booking."
                    });
                }

                var bookingProvider = await _context.Register
                    .FirstOrDefaultAsync(p =>
                        p.Id == model.ProviderId &&
                        p.Role == "Provider" &&
                        p.IsApproved &&
                        !p.IsBlocked);

                if (bookingProvider == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = "The selected provider could not be found or is not available."
                    });
                }

                // Always trust the provider name from the database.
                model.ProviderName = bookingProvider.Name ?? string.Empty;

                // ---------------------------------------------------------
                // Provider availability validation (server-side authority).
                // Booking is allowed only on the provider's registered days
                // and within the provider's registered working hours.
                // ---------------------------------------------------------
                if (model.BookingDate == default)
                {
                    return Json(new { success = false, message = "Booking date is required." });
                }

                if (model.BookingDate.Date < DateTime.Today)
                {
                    return Json(new { success = false, message = "A past date cannot be selected." });
                }

                string requestedServiceType = (model.ServiceType ?? string.Empty).Trim();
                bool isHomeService = requestedServiceType.Equals("Home Service", StringComparison.OrdinalIgnoreCase) ||
                                     requestedServiceType.Equals("Home", StringComparison.OrdinalIgnoreCase);
                bool isShopService = requestedServiceType.Equals("Shop Service", StringComparison.OrdinalIgnoreCase) ||
                                     requestedServiceType.Equals("Shop", StringComparison.OrdinalIgnoreCase);

                var availableDays = (bookingProvider.AvailableDays ?? string.Empty)
                    .Split(new[] { ',', ';', '|', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(d => d.Replace("\"", string.Empty).Trim().ToLowerInvariant())
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                string selectedDayFull = model.BookingDate.DayOfWeek.ToString().ToLowerInvariant();
                string selectedDayShort = selectedDayFull[..3];

                bool dayAllowed = availableDays.Count > 0 && availableDays.Any(d =>
                    d == selectedDayFull || d == selectedDayShort || d.StartsWith(selectedDayShort));

                if (!dayAllowed)
                {
                    return Json(new
                    {
                        success = false,
                        message = "The provider is not available on the selected day. Please review the provider's profile for available days and hours."
                    });
                }

                if (string.IsNullOrWhiteSpace(model.BookingTime))
                {
                    return Json(new { success = false, message = "Booking time is required." });
                }

                string bookingTimeValue = model.BookingTime.Trim();
                string[] timeFormats = { "H:mm", "HH:mm", "h:mm", "hh:mm", "h:mm tt", "hh:mm tt", "H:mm:ss", "HH:mm:ss", "h:mm:ss tt", "hh:mm:ss tt" };

                if (!DateTime.TryParseExact(bookingTimeValue, timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var selectedTime))
                {
                    if (!DateTime.TryParse(bookingTimeValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out selectedTime))
                        return Json(new { success = false, message = "Invalid booking time." });
                }

                DateTime? providerStart = ParseProviderTimeForBooking(bookingProvider.StartTime);
                DateTime? providerEnd = ParseProviderTimeForBooking(bookingProvider.EndTime);

                if (!providerStart.HasValue || !providerEnd.HasValue)
                {
                    return Json(new
                    {
                        success = false,
                        message = "The provider's working hours are not configured. Please review the provider's profile."
                    });
                }

                {
                    var selected = selectedTime.TimeOfDay;
                    if (selected < providerStart.Value.TimeOfDay || selected > providerEnd.Value.TimeOfDay)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "The provider is not available at the selected time. Please review the provider's profile for available days and hours."
                        });
                    }
                }

                // Shop Service: customer home address/location is optional.
                // Use the provider's shop address/coordinates as the booking location.
                if (isShopService)
                {
                    // Address is optional for Shop Service. If the customer
                    // leaves it empty, save the provider's shop address so
                    // the booking still has a valid service location.
                    if (string.IsNullOrWhiteSpace(model.Address))
                        model.Address = bookingProvider.ShopAddressInput ?? string.Empty;

                    // Remove a stale [Required] ModelState error for Address
                    // when the customer intentionally left Home Address empty.
                    ModelState.Remove(nameof(ServiceGo.Models.Booking.Address));

                    if (bookingProvider.ShopLat.HasValue && bookingProvider.ShopLng.HasValue)
                    {
                        model.Coordinates = $"{bookingProvider.ShopLat.Value.ToString(CultureInfo.InvariantCulture)},{bookingProvider.ShopLng.Value.ToString(CultureInfo.InvariantCulture)}";
                    }
                }
                else if (isHomeService)
                {
                    if (string.IsNullOrWhiteSpace(model.Address))
                        return Json(new { success = false, message = "Home Address is required for Home Service." });

                    if (string.IsNullOrWhiteSpace(model.Coordinates))
                        return Json(new { success = false, message = "Please select your exact home location on the map." });
                }

                // ---------------------------------------------------------
                // 2. Save uploaded problem image before authentication.
                //    The path is stored in PendingBooking, so signup/login
                //    cannot lose the uploaded file.
                // ---------------------------------------------------------
                string? savedImagePath = null;
                if (ProblemImage != null && ProblemImage.Length > 0)
                {
                    var result = await SaveFile(ProblemImage);
                    if (result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Json(new { success = false, message = result });
                    }

                    savedImagePath = result;
                }

                if (!string.IsNullOrEmpty(savedImagePath))
                    model.ImagePath = savedImagePath;

                // The IFormFile itself cannot be serialized into Session.
                model.ProblemImage = null;
                model.Id = 0;
                model.BookingId = null;

                // New booking always starts Pending.
                model.Status = "Pending";

                // Customer email is collected from the booking form for the
                // guest/authentication flow. It is normalized here so the
                // backend stores a clean value. After login/signup, the
                // authenticated account email remains authoritative.
                if (!string.IsNullOrWhiteSpace(model.CustomerEmail))
                    model.CustomerEmail = model.CustomerEmail.Trim().ToLowerInvariant();

                // ---------------------------------------------------------
                // 3. Validate the actual booking model before storing it.
                //    Customer fields are server-owned and are removed from
                //    ModelState because they are filled after authentication.
                // ---------------------------------------------------------
                ModelState.Remove("BookingId");
                ModelState.Remove("CustomerID");
                ModelState.Remove("CustomerName");
                ModelState.Remove("ProviderName");
                ModelState.Remove("ProblemImage");

                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage)
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .ToList();

                    return Json(new
                    {
                        success = false,
                        message = "Validation Failed: " + string.Join(", ", errors)
                    });
                }

                // ---------------------------------------------------------
                // 4. Guest booking: store the COMPLETE booking context and
                //    tell the browser to open Signup/Login. The booking form
                //    itself has already been completed and must not be lost.
                // ---------------------------------------------------------
                bool isCustomerLoggedIn =
                    string.Equals(sessionRole, "Customer", StringComparison.OrdinalIgnoreCase) &&
                    sessionUserId.HasValue &&
                    sessionUserId.Value > 0 &&
                    !string.IsNullOrWhiteSpace(sessionEmail);

                if (!isCustomerLoggedIn)
                {
                    var bookingData = JsonSerializer.Serialize(model);
                    HttpContext.Session.SetString("PendingBooking", bookingData);
                    HttpContext.Session.SetString("PendingBookingImagePath", savedImagePath ?? string.Empty);
                    HttpContext.Session.SetInt32("PendingBookingProviderId", model.ProviderId);

                    // Decide Login vs SignUp from the EMAIL entered in this booking.
                    // Do not use the browser cookie here: the cookie can exist even when
                    // the current booking email belongs to a different/new customer.
                    string bookingEmail = (model.CustomerEmail ?? string.Empty).Trim().ToLowerInvariant();

                    bool customerAccountExists = !string.IsNullOrWhiteSpace(bookingEmail) &&
                        await _context.CustomerSignup.AnyAsync(c =>
                            c.Email != null &&
                            c.Email.Trim().ToLower() == bookingEmail);

                    bool providerAccountExists = !string.IsNullOrWhiteSpace(bookingEmail) &&
                        await _context.ProviderSignup.AnyAsync(p =>
                            p.Email != null &&
                            p.Email.Trim().ToLower() == bookingEmail);

                    bool shouldLogin = customerAccountExists || providerAccountExists;

                    return Json(new
                    {
                        success = false,
                        needsSignup = !shouldLogin,
                        needsLogin = shouldLogin,
                        redirectUrl = shouldLogin
                            ? Url.Action("Login", "Home")
                            : Url.Action("SignUp", "Home"),
                        message = shouldLogin
                            ? "Please login to continue your booking."
                            : "Please sign up to continue your booking."
                    });
                }

                // ---------------------------------------------------------
                // 5. Logged-in customer. Load the account from DB.
                // ---------------------------------------------------------
                var customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Id == sessionUserId!.Value &&
                        c.Email != null &&
                        c.Email.Trim().ToLower() == sessionEmail!.ToLower());

                if (customer == null)
                {
                    return Json(new
                    {
                        success = false,
                        needsLogin = true,
                        redirectUrl = Url.Action("Login", "Home"),
                        message = "Your customer account could not be found. Please login again."
                    });
                }

                // ---------------------------------------------------------
                // 6. Logged-in customer: create the booking now.
                //    Profile completion is NOT required for booking.
                // ---------------------------------------------------------
                if (string.IsNullOrWhiteSpace(customer.CustomerId))
                {
                    customer.CustomerId = $"CUS-{customer.Id}";
                    await _context.SaveChangesAsync();
                }

                model.CustomerID = customer.CustomerId.Trim();
                model.CustomerName = customer.Name ?? string.Empty;
                model.CustomerEmail = customer.Email?.Trim();
                model.Id = 0;

                _context.Booking.Add(model);
                await _context.SaveChangesAsync();

                model.BookingId = $"BOOK-{model.Id}";
                await _context.SaveChangesAsync();

                // ---------------------------------------------------------
                // NEW BOOKING PROVIDER NOTIFICATION
                // ---------------------------------------------------------
                var providerNotificationUserId = !string.IsNullOrWhiteSpace(bookingProvider.ProviderId)
                    ? bookingProvider.ProviderId
                    : bookingProvider.Id.ToString();

                _context.Notifications.Add(new Notification
                {
                    UserId = providerNotificationUserId,
                    TargetUserId = providerNotificationUserId,
                    SenderUserId = customer.CustomerId ?? "Customer",
                    Title = "New Booking Request",
                    Message = $"{model.CustomerName ?? "Customer"} sent you a new booking request.",
                    NotificationType = "Booking",
                    IconClass = "fa-solid fa-calendar-plus",
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    BookingId = model.Id,
                    ReferenceId = model.BookingId,
                    ActionUrl = Url.Action("Dashboard", "Provider")
                });

                await _context.SaveChangesAsync();

                HttpContext.Session.SetString("UserEmail", customer.Email ?? string.Empty);
                HttpContext.Session.SetString("UserRole", "Customer");
                HttpContext.Session.SetString("UserCode", customer.CustomerId);
                HttpContext.Session.SetInt32("UserId", customer.Id);
                HttpContext.Session.Remove("PendingBooking");
                HttpContext.Session.Remove("PendingBookingImagePath");
                HttpContext.Session.Remove("PendingBookingProviderId");
                HttpContext.Session.Remove("PendingBookingAfterProfile");
                HttpContext.Session.Remove("PendingBookingAfterVerification");

                return Json(new
                {
                    success = true,
                    bookingId = model.BookingId,
                    redirectUrl = Url.Action("CusDashboard", "Customer"),
                    message = "Booking created successfully."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Booking Error: " + ex);
                return Json(new
                {
                    success = false,
                    message = "System Error: " + ex.Message
                });
            }
        }
        private async Task<string?> CreatePendingBookingForCustomerAsync(CustomerSignup customer)
        {
            var pendingBookingJson = HttpContext.Session.GetString("PendingBooking");

            if (string.IsNullOrWhiteSpace(pendingBookingJson))
                return null;

            try
            {
                var booking = JsonSerializer.Deserialize<Booking>(pendingBookingJson);

                if (booking == null || booking.ProviderId <= 0)
                    return null;

                var provider = await _context.Register
                    .FirstOrDefaultAsync(p =>
                        p.Id == booking.ProviderId &&
                        p.Role == "Provider" &&
                        p.IsApproved &&
                        !p.IsBlocked);

                if (provider == null)
                    return null;

                if (string.IsNullOrWhiteSpace(customer.CustomerId))
                    customer.CustomerId = $"CUS-{customer.Id}";

                booking.Id = 0;
                booking.BookingId = null;
                booking.CustomerID = customer.CustomerId.Trim();
                booking.CustomerName = customer.Name ?? string.Empty;
                booking.CustomerEmail = customer.Email?.Trim();
                booking.ProviderName = provider.Name ?? string.Empty;
                booking.Status = "Pending";
                var pendingImagePath =
                    HttpContext.Session.GetString("PendingBookingImagePath");

                if (!string.IsNullOrWhiteSpace(pendingImagePath))
                    booking.ImagePath = pendingImagePath;

                booking.ProblemImage = null;

                _context.Booking.Add(booking);
                await _context.SaveChangesAsync();

                booking.BookingId = $"BOOK-{booking.Id}";
                await _context.SaveChangesAsync();

                // ---------------------------------------------------------
                // NEW BOOKING PROVIDER NOTIFICATION
                // ---------------------------------------------------------
                var providerNotificationUserId = !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? provider.ProviderId
                    : provider.Id.ToString();

                _context.Notifications.Add(new Notification
                {
                    UserId = providerNotificationUserId,
                    TargetUserId = providerNotificationUserId,
                    SenderUserId = customer.CustomerId ?? "Customer",
                    Title = "New Booking Request",
                    Message = $"{booking.CustomerName ?? "Customer"} sent you a new booking request.",
                    // CustomerEmail is intentionally NOT included in the
                    // provider notification payload/message. It stays backend-only.
                    NotificationType = "Booking",
                    IconClass = "fa-solid fa-calendar-plus",
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    BookingId = booking.Id,
                    ReferenceId = booking.BookingId,
                    ActionUrl = Url.Action("Dashboard", "Provider")
                });

                await _context.SaveChangesAsync();

                HttpContext.Session.SetString("UserEmail", customer.Email ?? string.Empty);
                HttpContext.Session.SetString("UserRole", "Customer");
                HttpContext.Session.SetInt32("UserId", customer.Id);
                HttpContext.Session.SetString("UserCode", customer.CustomerId);

                HttpContext.Session.Remove("PendingBooking");
                HttpContext.Session.Remove("PendingBookingImagePath");
                HttpContext.Session.Remove("PendingBookingProviderId");
                HttpContext.Session.Remove("PendingBookingAfterProfile");
                HttpContext.Session.Remove("PendingBookingAfterVerification");

                return booking.BookingId;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ServiceGo Pending Booking Error: " + ex);
                return null;
            }
        }

        public IActionResult Privacy()
        {
            return View();
        }

        public IActionResult Terms()
        {
            return View();
        }

        [HttpGet]
        public IActionResult RegistrationPending()
        {
            return View();
        }
        [HttpGet]
        public IActionResult HelpCenter()
        {
            return View();
        }
    }
}






