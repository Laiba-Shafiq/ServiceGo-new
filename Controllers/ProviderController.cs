using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceGo.Data;
using ServiceGo.Models;

namespace ServiceGo.Controllers
{
    public class ProviderController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IDataProtector _providerSessionProtector;

        public ProviderController(
            AppDbContext context,
            IDataProtectionProvider dataProtectionProvider)
        {
            _context = context;
            _providerSessionProtector =
                dataProtectionProvider.CreateProtector("ServiceGo.ProviderSession.v1");
        }

        // Helper methods

        private async Task<Register?> GetLoggedInProviderAsync()
        {
            // Primary: session email
            var email = HttpContext.Session.GetString("UserEmail");

            if (!string.IsNullOrWhiteSpace(email))
            {
                var providerByEmail = await _context.Register
                    .FirstOrDefaultAsync(p =>
                        p.Email == email &&
                        p.Role == "Provider");

                if (providerByEmail != null)
                    return providerByEmail;
            }

            // Secondary: session user id
            var sessionUserId = HttpContext.Session.GetInt32("UserId");

            if (sessionUserId.HasValue && sessionUserId.Value > 0)
            {
                var providerById = await _context.Register
                    .FirstOrDefaultAsync(p =>
                        p.Id == sessionUserId.Value &&
                        p.Role == "Provider");

                if (providerById != null)
                    return providerById;
            }

            // Reload-safe fallback: if authentication cookie/identity is still
            // available, use the authenticated email even when session data
            // has been lost/expired.
            var identityEmail =
                User?.Identity?.IsAuthenticated == true
                    ? User.Identity?.Name
                    : null;

            if (!string.IsNullOrWhiteSpace(identityEmail))
            {
                var providerByIdentity = await _context.Register
                    .FirstOrDefaultAsync(p =>
                        p.Email == identityEmail &&
                        p.Role == "Provider");

                if (providerByIdentity != null)
                    return providerByIdentity;
            }

            // Final fallback: use the protected provider-session cookie.
            // This keeps provider actions working even if the ASP.NET
            // session has expired while the provider dashboard is open.
            var protectedProviderId =
                Request.Cookies["ServiceGoProviderSession"];

            if (!string.IsNullOrWhiteSpace(protectedProviderId))
            {
                try
                {
                    var providerIdText =
                        _providerSessionProtector.Unprotect(protectedProviderId);

                    if (int.TryParse(providerIdText, out var providerId) &&
                        providerId > 0)
                    {
                        var providerByCookie = await _context.Register
                            .FirstOrDefaultAsync(p =>
                                p.Id == providerId &&
                                p.Role == "Provider");

                        if (providerByCookie != null)
                            return providerByCookie;
                    }
                }
                catch
                {
                    // Ignore an invalid/expired protected cookie.
                }
            }

            return null;
        }

        // Customer email and phone are not stored in the Booking record.
        // When a provider-side notification or email requires customer data,
        // the verified customer account is used to look up the email address.
        private async Task<string?> GetCustomerEmailAsync(string? customerId)
        {
            if (string.IsNullOrWhiteSpace(customerId))
                return null;

            string code = customerId.Trim();
            string numericIdText = code.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase)
                ? code.Substring(4).Trim()
                : (code.StartsWith("CUS", StringComparison.OrdinalIgnoreCase)
                    ? code.Substring(3).Trim()
                    : code);

            int numericId;
            bool hasNumericId = int.TryParse(numericIdText, out numericId);

            var customer = await _context.CustomerSignup
                .AsNoTracking()
                .FirstOrDefaultAsync(c =>
                    (!string.IsNullOrWhiteSpace(c.CustomerId) &&
                     c.CustomerId.Trim() == code) ||
                    (hasNumericId && c.Id == numericId));

            return customer?.Email;
        }

        // Resolve customer ID for messaging
        private async Task<CustomerSignup?> ResolveCustomerForMessagingAsync(string? customerCode)
        {
            if (string.IsNullOrWhiteSpace(customerCode))
                return null;

            string code = customerCode.Trim();
            string numericCode = code;

            if (numericCode.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                numericCode = numericCode.Substring(4).Trim();
            else if (numericCode.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                numericCode = numericCode.Substring(3).Trim();

            bool hasNumericId = int.TryParse(numericCode, out int customerDbId);

            return await _context.CustomerSignup
                .AsNoTracking()
                .FirstOrDefaultAsync(c =>
                    (!string.IsNullOrWhiteSpace(c.CustomerId) &&
                     c.CustomerId.Trim() == code) ||
                    (hasNumericId && c.Id == customerDbId));
        }

        private static List<string> GetCustomerMessageIds(CustomerSignup customer)
        {
            var ids = new List<string>();

            if (!string.IsNullOrWhiteSpace(customer.CustomerId))
                ids.Add(customer.CustomerId.Trim());

            ids.Add(customer.Id.ToString());
            ids.Add("CUS-" + customer.Id);
            ids.Add("CUS" + customer.Id);

            return ids
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Notification helper
        // Provider notification filter

        private static bool IsAllowedProviderNotification(Notification n)
        {
            var type = (n.NotificationType ?? string.Empty).Trim().ToLowerInvariant();
            var title = (n.Title ?? string.Empty).Trim().ToLowerInvariant();

            return type is "message" or "booking" or "complaint" or "warning" or "reschedule"
                or "cancellation" or "rating" or "wallet"
                || title.Contains("message")
                || title.Contains("wallet")
                || title.Contains("complaint")
                || title.Contains("warning")
                || title.Contains("reschedule")
                || title.Contains("cancel")
                || title.Contains("rating")
                || title.Contains("customer accepted")
                || title.Contains("customer rejected");
        }

        private void AddNotification(
            string targetUserId,
            string title,
            string message,
            string notificationType,
            string iconClass,
            string senderUserId,
            int? bookingId = null,
            string? referenceId = null,
            string? actionUrl = null)
        {
            if (string.IsNullOrWhiteSpace(targetUserId))
                return;

            _context.Notifications.Add(new Notification
            {
                UserId = targetUserId,
                TargetUserId = targetUserId,
                SenderUserId = senderUserId,
                Title = title,
                Message = message,
                NotificationType = notificationType,
                IconClass = iconClass,
                IsRead = false,
                Timestamp = DateTime.Now,
                BookingId = bookingId,
                ReferenceId = referenceId,
                ActionUrl = actionUrl
            });
        }

        private async Task<int> GetProviderUnreadNotificationCountAsync(Register provider)
        {
            if (provider == null)
                return 0;

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            return await _context.Notifications
                .AsNoTracking()
                .CountAsync(n =>
                    !n.IsRead &&
                    (
                        n.TargetUserId == providerId ||
                        n.TargetUserId == providerEmail ||
                        n.TargetUserId == providerCode ||
                        n.UserId == providerId ||
                        n.UserId == providerEmail ||
                        n.UserId == providerCode
                    ) &&
                    n.SenderUserId != providerId &&
                    n.SenderUserId != providerEmail &&
                    n.SenderUserId != providerCode);
        }

        private static bool TryParseFlexibleTime(
            string? timeText,
            out TimeSpan time)
        {
            time = default;

            if (string.IsNullOrWhiteSpace(timeText))
                return false;

            string start = timeText.Trim();
            int dashIndex = start.IndexOf('-');
            if (dashIndex > 0)
                start = start[..dashIndex].Trim();

            start = System.Text.RegularExpressions.Regex.Replace(
                start, @"\s+", " ").Trim();

            string[] formats =
            {
                "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt",
                "h tt", "hh tt",
                "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss",
                "h:mm", "hh:mm", "h", "hh"
            };

            if (DateTime.TryParseExact(
                    start, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                time = parsed.TimeOfDay;
                return true;
            }

            if (DateTime.TryParse(
                    start, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                time = parsed.TimeOfDay;
                return true;
            }

            return false;
        }

        private static DateTime? GetBookingDateTime(
            DateTime bookingDate,
            string? bookingTime)
        {
            if (!TryParseFlexibleTime(
                    bookingTime,
                    out TimeSpan time))
                return null;

            return bookingDate.Date.Add(time);
        }

        private static bool IsRescheduleAllowed(
            DateTime bookingDate,
            string? bookingTime)
        {
            var bookingDateTime =
                GetBookingDateTime(
                    bookingDate,
                    bookingTime);

            if (!bookingDateTime.HasValue)
                return false;

            return DateTime.Now <
                   bookingDateTime.Value.AddHours(-2);
        }

        private static bool ShouldExpirePendingBooking(
            DateTime bookingDate,
            string? bookingTime)
        {
            var bookingDateTime =
                GetBookingDateTime(
                    bookingDate,
                    bookingTime);

            if (!bookingDateTime.HasValue)
                return false;

            return DateTime.Now >=
                   bookingDateTime.Value;
        }

        // Find provider booking

        private async Task<Booking?> FindProviderBookingAsync(
            int bookingId,
            int providerId)
        {
            if (bookingId <= 0)
                return null;

            return await _context.Booking
                .FirstOrDefaultAsync(b =>
                    b.Id == bookingId &&
                    b.ProviderId == providerId);
        }

        // Dashboard

        [HttpGet]
        public async Task<IActionResult> Dashboard()
        {
            // Use the central reload-safe provider lookup so refreshing the
            // provider dashboard does not unnecessarily send the user to login.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction(
                    "Login",
                    "Home");
            }

            // Provider Dashboard is available only after admin approval.
            // Incomplete, pending, or rejected providers stay on Provider Home.
            if (!provider.IsApproved)
            {
                return RedirectToAction("Index", "Home");
            }

            // Keep a protected provider identity cookie available so provider
            // actions can recover if the ASP.NET session expires.
            Response.Cookies.Append(
                "ServiceGoProviderSession",
                _providerSessionProtector.Protect(
                    provider.Id.ToString()),
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });

            ViewBag.UnreadCount = await GetProviderUnreadNotificationCountAsync(provider);

            var providerBookings =
                await _context.Booking
                    .Where(b => b.ProviderId == provider.Id)
                    .OrderByDescending(b => b.Id)
                    .ToListAsync();

            var pendingBookings = providerBookings
                .Where(b =>
                    string.IsNullOrWhiteSpace(b.Status) ||
                    string.Equals(
                        b.Status.Trim(),
                        "Pending",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool changed = false;

            foreach (var booking in pendingBookings)
            {
                if (ShouldExpirePendingBooking(
                    booking.BookingDate,
                    booking.BookingTime))
                {
                    booking.Status = "Expired";
                    booking.RescheduledStatus = "Expired";

                    booking.RequestedDate = null;
                    booking.RequestedTime = null;

                    booking.RescheduleRequestedBy = null;

                    booking.IsRescheduledByCustomer = false;
                    booking.IsRescheduledByProvider = false;

                    // Send the expiry notification once. It is informational only.
                    bool expiryNotificationExists = await _context.Notifications
                        .AnyAsync(n =>
                            n.BookingId == booking.Id &&
                            n.Title == "Booking Expired" &&
                            (n.TargetUserId == provider.Id.ToString() ||
                             n.UserId == provider.Id.ToString()));

                    if (!expiryNotificationExists)
                    {
                        AddNotification(
                            provider.Id.ToString(),
                            "Booking Expired",
                            $"Booking {booking.BookingId ?? booking.Id.ToString()} expired because its scheduled date and time passed without being accepted.",
                            "Booking",
                            "fa-solid fa-clock",
                            "System",
                            booking.Id,
                            booking.BookingId,
                            null);
                    }

                    changed = true;
                }
            }

            if (changed)
            {
                await _context.SaveChangesAsync();
            }

            var pendingCount = providerBookings
                .Count(b =>
                    string.IsNullOrWhiteSpace(b.Status) ||
                    string.Equals(
                        b.Status.Trim(),
                        "Pending",
                        StringComparison.OrdinalIgnoreCase));

            var lastComplaint = await _context.Complaints
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync();

            int nextComplaintId = lastComplaint != null
                ? lastComplaint.Id + 1
                : 1;

            ViewBag.NextComplaintId = $"COMP-{nextComplaintId}";

            var model = new ProviderProfile
            {
                Id = provider.Id,

                ProviderId =
                    !string.IsNullOrWhiteSpace(provider.ProviderId)
                        ? (provider.ProviderId.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase)
                            ? provider.ProviderId
                            : "PRO-" + provider.ProviderId)
                        : "PRO-" + provider.Id,

                FullName = provider.Name,
                Name = provider.Name,

                Location =
                    provider.LocalAddress ??
                    provider.City,

                Phone = provider.PhoneNumber,
                PhoneNumber = provider.PhoneNumber,

                Email = provider.Email,

                ProfilePicPath = provider.ProfilePicPath,

                WalletBalance = provider.WalletBalance,

                PendingApprovalsCount = pendingCount
            };

            return View("Dashboard", model);
        }

        // GET booking details

        [HttpGet]
        public async Task<IActionResult> GetBookingDetails(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            // Same extra fields the dashboard list (GetActiveJobs) sends, so a
            // booking opened from a notification behaves exactly like one opened
            // from the dashboard (Accept button, map, location, etc.).
            string? detailLat = null;
            string? detailLng = null;

            if (!string.IsNullOrWhiteSpace(booking.Coordinates))
            {
                var detailCoordinates = booking.Coordinates.Split(',');

                if (detailCoordinates.Length >= 1)
                    detailLat = detailCoordinates[0].Trim();

                if (detailCoordinates.Length >= 2)
                    detailLng = detailCoordinates[1].Trim();
            }

            var detailStatus =
                string.IsNullOrWhiteSpace(booking.Status)
                    ? "Pending"
                    : booking.Status;

            bool detailIsPending =
                string.IsNullOrWhiteSpace(booking.Status) ||
                string.Equals(booking.Status.Trim(), "Pending", StringComparison.OrdinalIgnoreCase);

            return Ok(new
            {
                success = true,

                // Fixed platform commission (Rs. 10 per accepted booking).
                // Without this the Accept button stayed disabled when a booking
                // was opened from a notification.
                commission = "10",

                databaseId = booking.Id,

                customerFormattedId =
                    !string.IsNullOrWhiteSpace(booking.CustomerID)
                        ? (booking.CustomerID.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase)
                            ? booking.CustomerID
                            : "CUS-" + booking.CustomerID)
                        : string.Empty,

                location = booking.Address,
                problemDescription = booking.ProblemDetails,
                problemImage = booking.ImagePath,

                liveLocationUrl =
                    !string.IsNullOrWhiteSpace(booking.Coordinates)
                        ? $"https://www.google.com/maps?q={booking.Coordinates}"
                        : null,

                lat = detailLat,
                lng = detailLng,

                date = booking.BookingDate.ToString("yyyy-MM-dd"),
                time = booking.BookingTime,

                bookingStatus = detailStatus,
                jobStatus = detailStatus,

                canAccept = detailIsPending,
                canReject = detailIsPending,

                canMarkAsComplete =
                    string.Equals(booking.Status?.Trim(), "Accepted", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(booking.RescheduledStatus?.Trim(), "Pending", StringComparison.OrdinalIgnoreCase),

                canCancel = IsCancellationAllowed(booking),

                providerId = provider.Id,
                providerName = provider.Name,
                providerPhone = provider.PhoneNumber,

                id = booking.Id,

                bookingId =
                    !string.IsNullOrWhiteSpace(booking.BookingId)
                        ? booking.BookingId
                        : booking.Id.ToString(),

                customerId = booking.CustomerID,

                customerName =
                    !string.IsNullOrWhiteSpace(booking.CustomerName)
                        ? booking.CustomerName
                        : "Customer",

                customerEmail = await GetCustomerEmailAsync(booking.CustomerID),

                serviceName = booking.ServiceName,

                serviceType = booking.ServiceType,

                issueDescription = booking.ProblemDetails,

                issueImage = booking.ImagePath,

                address = booking.Address,

                coordinates = booking.Coordinates,

                distance = booking.Distance,

                visitCharge = booking.VisitCharges,

                bookingDate =
                    booking.BookingDate.ToString("yyyy-MM-dd"),

                bookingTime = booking.BookingTime,

                status =
                    string.IsNullOrWhiteSpace(booking.Status)
                        ? "Pending"
                        : booking.Status,

                rescheduledStatus =
                    booking.RescheduledStatus,

                requestedDate =
                    booking.RequestedDate.HasValue
                        ? booking.RequestedDate.Value
                            .ToString("yyyy-MM-dd")
                        : null,

                requestedTime =
                    booking.RequestedTime,

                rescheduleRequestedBy =
                    booking.RescheduleRequestedBy
            });
        }

        // Active jobs

        [HttpGet]
        public async Task<IActionResult> GetActiveJobs()
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Json(new List<object>());
            }

            // Load all provider bookings by provider ID first.
            // Normalize status values so casing and extra spaces do not hide pending requests.
            var providerBookings = await _context.Booking
                .Where(b => b.ProviderId == provider.Id)
                .OrderByDescending(b => b.Id)
                .ToListAsync();

            var pendingBookings = providerBookings
                .Where(b => string.IsNullOrWhiteSpace(b.Status) ||
                            string.Equals(b.Status.Trim(), "Pending", StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool expiryChanged = false;

            foreach (var booking in pendingBookings)
            {
                if (ShouldExpirePendingBooking(
                    booking.BookingDate,
                    booking.BookingTime))
                {
                    booking.Status = "Expired";
                    booking.RescheduledStatus = "Expired";

                    booking.RequestedDate = null;
                    booking.RequestedTime = null;

                    booking.RescheduleRequestedBy = null;

                    booking.IsRescheduledByCustomer = false;
                    booking.IsRescheduledByProvider = false;

                    bool expiryNotificationExists = await _context.Notifications
                        .AnyAsync(n =>
                            n.BookingId == booking.Id &&
                            n.Title == "Booking Expired" &&
                            (n.TargetUserId == provider.Id.ToString() ||
                             n.UserId == provider.Id.ToString()));

                    if (!expiryNotificationExists)
                    {
                        AddNotification(
                            provider.Id.ToString(),
                            "Booking Expired",
                            $"Booking {booking.BookingId ?? booking.Id.ToString()} expired because its scheduled date and time passed without being accepted.",
                            "Booking",
                            "fa-solid fa-clock",
                            "System",
                            booking.Id,
                            booking.BookingId,
                            null);
                    }

                    expiryChanged = true;
                }
            }

            if (expiryChanged)
            {
                await _context.SaveChangesAsync();
            }

            // Always include Pending bookings on the active dashboard,
            // whether the database contains "pending", " Pending ", or NULL.
            // Accepted and reschedule statuses are also loaded only for this provider.
            var bookings = providerBookings
                .Where(b =>
                {
                    var status = b.Status?.Trim() ?? string.Empty;

                    return string.IsNullOrWhiteSpace(status) ||
                           string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Reschedule Requested", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Rescheduled", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(b => b.Id)
                .ToList();

            var customerEmails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var customerCodes = bookings
                .Select(b => b.CustomerID?.Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (customerCodes.Count > 0)
            {
                var customers = await _context.CustomerSignup
                    .AsNoTracking()
                    .Where(c => c.Email != null)
                    .ToListAsync();

                foreach (var customer in customers)
                {
                    if (!string.IsNullOrWhiteSpace(customer.CustomerId) &&
                        !string.IsNullOrWhiteSpace(customer.Email))
                    {
                        customerEmails[customer.CustomerId.Trim()] = customer.Email.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(customer.Email))
                    {
                        customerEmails[customer.Id.ToString()] = customer.Email.Trim();
                        customerEmails[$"CUS{customer.Id}"] = customer.Email.Trim();
                        customerEmails[$"CUS-{customer.Id}"] = customer.Email.Trim();
                    }
                }
            }

            var activeJobs = bookings.Select(b =>
            {
                string? lat = null;
                string? lng = null;

                if (!string.IsNullOrWhiteSpace(b.Coordinates))
                {
                    var coordinates = b.Coordinates.Split(',');

                    if (coordinates.Length >= 1)
                    {
                        lat = coordinates[0].Trim();
                    }

                    if (coordinates.Length >= 2)
                    {
                        lng = coordinates[1].Trim();
                    }
                }

                decimal visitCharge = 0;

                decimal.TryParse(
                    Convert.ToString(b.VisitCharges),
                    out visitCharge);

                string? requestedBy = b.RescheduleRequestedBy;

                if (string.IsNullOrWhiteSpace(requestedBy))
                {
                    if (b.IsRescheduledByCustomer)
                    {
                        requestedBy = "Customer";
                    }
                    else if (b.IsRescheduledByProvider)
                    {
                        requestedBy = "Provider";
                    }
                }

                bool hasPendingReschedule =
                    string.Equals(b.RescheduledStatus, "Pending", StringComparison.OrdinalIgnoreCase) &&
                    (
                        b.IsRescheduledByCustomer ||
                        b.IsRescheduledByProvider ||
                        !string.IsNullOrWhiteSpace(b.RescheduleRequestedBy)
                    );

                bool customerRescheduleRequest =
                    hasPendingReschedule &&
                    string.Equals(
                        requestedBy,
                        "Customer",
                        StringComparison.OrdinalIgnoreCase);

                bool providerRescheduleRequest =
                    hasPendingReschedule &&
                    string.Equals(
                        requestedBy,
                        "Provider",
                        StringComparison.OrdinalIgnoreCase);

                bool pendingRescheduleAllowed =
                    (string.IsNullOrWhiteSpace(b.Status) ||
                     string.Equals(b.Status.Trim(), "Pending", StringComparison.OrdinalIgnoreCase)) &&
                    !hasPendingReschedule &&
                    !(
                        b.RescheduledDate.HasValue ||
                        string.Equals(
                            b.RescheduledStatus,
                            "Rejected",
                            StringComparison.OrdinalIgnoreCase)
                    );

                // Reschedule is available ONLY for Pending bookings.
                // Accepted bookings must never show/request Reschedule.
                bool rescheduleAllowed =
                    pendingRescheduleAllowed;

                string? requestedNewDate = null;

                if (b.RequestedDate.HasValue)
                {
                    requestedNewDate = b.RequestedDate.Value.ToString("yyyy-MM-dd");
                }

                string? requestedNewTime = !string.IsNullOrWhiteSpace(b.RequestedTime)
                    ? b.RequestedTime.Trim()
                    : null;

                // Booking action windows. Pending can be cancelled anytime;
                // accepted bookings can be cancelled only before the final 2-hour window.
                bool canCancel = IsCancellationAllowed(b);

                // Provider can request a reschedule only once for a booking.
                bool rescheduleUsed =
                    b.RescheduledDate.HasValue ||
                    string.Equals(b.RescheduledStatus, "Rejected", StringComparison.OrdinalIgnoreCase);

                return new
                {
                    id = b.Id,

                    bookingId = !string.IsNullOrWhiteSpace(b.BookingId)
                        ? b.BookingId
                        : b.Id.ToString(),

                    databaseId = b.Id,

                    customerId = b.CustomerID,

                    customerFormattedId = !string.IsNullOrWhiteSpace(b.CustomerID)
                        ? (b.CustomerID.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase)
                            ? b.CustomerID
                            : "CUS-" + b.CustomerID)
                        : string.Empty,

                    customerName = !string.IsNullOrWhiteSpace(b.CustomerName)
                        ? b.CustomerName
                        : "Customer",

                    customerEmail = !string.IsNullOrWhiteSpace(b.CustomerID) &&
                                    customerEmails.TryGetValue(b.CustomerID.Trim(), out var customerEmailValue)
                        ? customerEmailValue
                        : (!string.IsNullOrWhiteSpace(b.CustomerID) &&
                           (b.CustomerID.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase) ||
                             b.CustomerID.StartsWith("CUS", StringComparison.OrdinalIgnoreCase)) &&
                           customerEmails.TryGetValue(b.CustomerID.Substring(4).Trim(), out var numericEmailValue)
                            ? numericEmailValue
                            : null),

                    serviceName = b.ServiceName,
                    serviceType = b.ServiceType,

                    issueDescription = b.ProblemDetails,
                    problemDescription = b.ProblemDetails,

                    issueImage = b.ImagePath,
                    problemImage = b.ImagePath,

                    location = b.Address,
                    address = b.Address,

                    liveLocationUrl = !string.IsNullOrWhiteSpace(b.Coordinates)
                        ? $"https://www.google.com/maps?q={b.Coordinates}"
                        : null,

                    lat = lat,
                    lng = lng,

                    distance = !string.IsNullOrWhiteSpace(b.Distance)
                        ? b.Distance
                        : "0",

                    visitCharge = visitCharge.ToString("0.##"),

                    commission = "10",

                    date = b.BookingDate.ToString("yyyy-MM-dd"),

                    time = b.BookingTime,

                    bookingDate = b.BookingDate.ToString("yyyy-MM-dd"),

                    bookingTime = b.BookingTime,

                    status = string.IsNullOrWhiteSpace(b.Status)
                        ? "Pending"
                        : b.Status,

                    bookingStatus = string.IsNullOrWhiteSpace(b.Status)
                        ? "Pending"
                        : b.Status,

                    jobStatus = string.IsNullOrWhiteSpace(b.Status)
                        ? "Pending"
                        : b.Status,

                    completedAt = b.CompletedAt,
                    cancelledAt = b.CancelledAt,
                    cancelledBy = b.CancelledBy,
                    rejectedAt = b.RejectedAt,

                    // CUSTOMER request only.
                    // The provider's own pending request must not set this field to TRUE.
                    hasRescheduleRequest = customerRescheduleRequest,

                    rescheduleRequestedBy = requestedBy,

                    requestedBy = requestedBy,

                    rescheduledStatus = b.RescheduledStatus,

                    requestedNewDate = requestedNewDate,

                    requestedNewTime = requestedNewTime,

                    requestedDate = requestedNewDate,

                    requestedTime = requestedNewTime,

                    providerCanViewReschedule = customerRescheduleRequest,

                    providerOwnRescheduleRequest = providerRescheduleRequest,

                    rescheduleAllowed = rescheduleAllowed && !rescheduleUsed,
                    rescheduleUsed = rescheduleUsed,

                    canAccept =
                        string.IsNullOrWhiteSpace(b.Status) ||
                        string.Equals(
                            b.Status.Trim(),
                            "Pending",
                            StringComparison.OrdinalIgnoreCase),

                    canReject =
                        string.IsNullOrWhiteSpace(b.Status) ||
                        string.Equals(
                            b.Status.Trim(),
                            "Pending",
                            StringComparison.OrdinalIgnoreCase),

                    canMarkAsComplete =
                        string.Equals(
                            b.Status?.Trim(),
                            "Accepted",
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(
                            b.RescheduledStatus?.Trim(),
                            "Pending",
                            StringComparison.OrdinalIgnoreCase),

                    canCancel = canCancel,

                    providerId = provider.Id,
                    providerName = provider.Name,
                    providerPhone = provider.PhoneNumber
                };
            }).ToList();

            return Json(activeJobs);
        }

        // Provider cancellation window

        private bool IsCancellationAllowed(Booking booking)
        {
            if (booking == null)
                return false;

            if (string.Equals(booking.Status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(booking.Status))
            {
                return true;
            }

            if (!string.Equals(booking.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
                return false;

            var bookingDateTime = GetBookingDateTime(booking.BookingDate, booking.BookingTime);
            if (!bookingDateTime.HasValue)
                return false;

            return DateTime.Now < bookingDateTime.Value.AddHours(-2);
        }

        private bool IsRescheduleAllowedForAcceptedBooking(Booking booking)
        {
            if (booking == null ||
                !string.Equals(booking.Status?.Trim(), "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (booking.RescheduledDate.HasValue ||
                string.Equals(booking.RescheduledStatus?.Trim(), "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(booking.RescheduledStatus?.Trim(), "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var bookingDateTime = GetBookingDateTime(booking.BookingDate, booking.BookingTime);
            return bookingDateTime.HasValue && DateTime.Now < bookingDateTime.Value.AddHours(-2);
        }

        // Provider requests reschedule

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestReschedule(
            [FromBody] RescheduleRequest request)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            if (request == null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid reschedule request."
                });
            }

            int bookingId = request.BookingId;

            if (bookingId <= 0 &&
                request.Id.HasValue)
            {
                bookingId = request.Id.Value;
            }

            if (bookingId <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Valid booking ID is required."
                });
            }

            // Zero wallet balance: pending booking requests are locked.
            if (provider.WalletBalance <= 0)
            {
                return StatusCode(403, new
                {
                    success = false,
                    message =
                        "Your wallet balance is Rs. 0. Please add money to your wallet to respond to booking requests.",
                    walletBalance = provider.WalletBalance
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found for this provider.",
                    bookingId = bookingId
                });
            }

            bool isPending =
                string.IsNullOrWhiteSpace(booking.Status) ||
                string.Equals(
                    booking.Status.Trim(),
                    "Pending",
                    StringComparison.OrdinalIgnoreCase);

            bool isAccepted =
                string.Equals(
                    booking.Status?.Trim(),
                    "Accepted",
                    StringComparison.OrdinalIgnoreCase);

            // Reschedule is allowed only while the booking is Pending.
            // Once the booking is Accepted, neither side can request
            // another reschedule from this action.
            if (!isPending)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Only pending bookings can be rescheduled.",
                    bookingStatus = booking.Status
                });
            }

            if (isPending &&
                ShouldExpirePendingBooking(
                    booking.BookingDate,
                    booking.BookingTime))
            {
                booking.Status = "Expired";
                booking.RescheduledStatus = "Expired";

                booking.RequestedDate = null;
                booking.RequestedTime = null;

                booking.RescheduleRequestedBy = null;

                booking.IsRescheduledByCustomer = false;
                booking.IsRescheduledByProvider = false;

                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message =
                        "This booking has expired and can no longer be rescheduled."
                });
            }

            if (booking.RescheduledStatus == "Pending")
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "A reschedule request is already pending."
                });
            }

            // A provider may request reschedule only once for a booking.
            // A completed/accepted reschedule is recorded in RescheduledDate;
            // a customer rejection is recorded as RescheduledStatus=Rejected.
            if (booking.RescheduledDate.HasValue ||
                string.Equals(booking.RescheduledStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "This booking has already used its one reschedule request."
                });
            }

            if (!request.NewDate.HasValue)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "New date is required."
                });
            }

            if (string.IsNullOrWhiteSpace(
                request.NewTime))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "New time is required."
                });
            }

            if (!TryParseFlexibleTime(
                request.NewTime.Trim(),
                out TimeSpan newTime))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid time. Please use AM/PM format, for example 10:00 AM."
                });
            }

            string normalizedNewTime =
                DateTime.Today.Add(newTime)
                    .ToString("h:mm tt", CultureInfo.InvariantCulture);

            DateTime newDateTime =
                request.NewDate.Value.Date
                    .Add(newTime);

            if (newDateTime <= DateTime.Now)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "New booking date/time must be in the future."
                });
            }

            booking.RequestedDate =
                request.NewDate.Value.Date;

            booking.RequestedTime =
                normalizedNewTime;

            booking.RescheduleRequestedBy =
                "Provider";

            booking.IsRescheduledByProvider =
                true;

            booking.IsRescheduledByCustomer =
                false;

            booking.RescheduledStatus =
                "Pending";

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Provider Reschedule Request",
                    $"{provider.Name ?? "Provider"} requested to reschedule booking {booking.BookingId ?? booking.Id.ToString()} to {booking.RequestedDate:dd MMM yyyy} at {booking.RequestedTime}.",
                    "Reschedule",
                    "fa-solid fa-calendar-days",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Reschedule request sent to customer successfully.",

                bookingId = booking.Id,

                customerId = booking.CustomerID,

                customerEmail = await GetCustomerEmailAsync(booking.CustomerID),

                customerName = booking.CustomerName,

                rescheduleRequestedBy = "Provider",

                rescheduleStatus = "Pending",

                requestedDate =
                    booking.RequestedDate.Value
                        .ToString("yyyy-MM-dd"),

                requestedTime =
                    booking.RequestedTime,

                bookingStatus =
                    string.IsNullOrWhiteSpace(booking.Status)
                        ? "Pending"
                        : booking.Status
            });
        }

        // Old reschedule method

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RescheduleBooking(
            int jobId,
            DateTime newDate,
            string newTime)
        {
            var request =
                new RescheduleRequest
                {
                    BookingId = jobId,
                    NewDate = newDate,
                    NewTime = newTime
                };

            return await RequestReschedule(request);
        }

        // Provider accepts customer reschedule

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptReschedule(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            // Zero wallet balance: pending booking requests are locked.
            if (provider.WalletBalance <= 0)
            {
                return StatusCode(403, new
                {
                    success = false,
                    message =
                        "Your wallet balance is Rs. 0. Please add money to your wallet to respond to booking requests.",
                    walletBalance = provider.WalletBalance
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            bool isCustomerRequest =
                booking.IsRescheduledByCustomer ||
                string.Equals(
                    booking.RescheduleRequestedBy,
                    "Customer",
                    StringComparison.OrdinalIgnoreCase);

            if (!isCustomerRequest ||
                !string.Equals(
                    booking.RescheduledStatus?.Trim(),
                    "Pending",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "No pending customer reschedule request found."
                });
            }

            if (!booking.RequestedDate.HasValue ||
                string.IsNullOrWhiteSpace(
                    booking.RequestedTime))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Requested date/time is missing."
                });
            }

            if (!TryParseFlexibleTime(
                booking.RequestedTime.Trim(),
                out TimeSpan requestedTime))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Requested time is invalid."
                });
            }

            DateTime newDateTime =
                booking.RequestedDate.Value.Date
                    .Add(requestedTime);

            if (newDateTime <= DateTime.Now)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Requested booking date/time has already passed."
                });
            }

            // Original booking time already passed: the card must expire,
            // not be accepted.
            if (ShouldExpirePendingBooking(
                booking.BookingDate,
                booking.BookingTime))
            {
                booking.Status = "Expired";
                booking.RescheduledStatus = "Expired";
                booking.RequestedDate = null;
                booking.RequestedTime = null;
                booking.RescheduleRequestedBy = null;
                booking.IsRescheduledByCustomer = false;
                booking.IsRescheduledByProvider = false;

                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message =
                        "This booking has expired and can no longer be accepted."
                });
            }

            // Accepting a booking costs the fixed Rs.10 commission
            // (same rule as AcceptJob and the customer-side accept).
            const decimal rescheduleCommission = 10m;

            if (!booking.WalletCommissionDeducted)
            {
                if (provider.WalletBalance < rescheduleCommission)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Insufficient wallet balance. Rs.10 is required to accept this booking.",
                        walletBalance = provider.WalletBalance,
                        commission = rescheduleCommission
                    });
                }

                provider.WalletBalance -= rescheduleCommission;
                booking.WalletCommissionDeducted = true;
            }

            booking.BookingDate =
                booking.RequestedDate.Value.Date;

            booking.BookingTime =
                DateTime.Today.Add(requestedTime)
                    .ToString("h:mm tt", CultureInfo.InvariantCulture);

            booking.RescheduledDate =
                booking.BookingDate;

            booking.RescheduledTime =
                booking.BookingTime;

            booking.RescheduledStatus =
                "Accepted";

            booking.RequestedDate = null;
            booking.RequestedTime = null;

            booking.RescheduleRequestedBy = null;

            booking.IsRescheduledByCustomer = false;
            booking.IsRescheduledByProvider = false;

            booking.Status = "Accepted";

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Reschedule Accepted",
                    $"Your reschedule request for booking {booking.BookingId ?? booking.Id.ToString()} has been accepted.",
                    "Reschedule",
                    "fa-solid fa-calendar-check",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Customer reschedule accepted successfully.",

                bookingId = booking.Id,

                newDate =
                    booking.BookingDate
                        .ToString("yyyy-MM-dd"),

                newTime =
                    booking.BookingTime,

                bookingStatus =
                    booking.Status,

                rescheduleStatus =
                    booking.RescheduledStatus,

                walletBalance =
                    provider.WalletBalance
            });
        }

        // Provider rejects customer reschedule

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectReschedule(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            // Zero wallet balance: pending booking requests are locked.
            if (provider.WalletBalance <= 0)
            {
                return StatusCode(403, new
                {
                    success = false,
                    message =
                        "Your wallet balance is Rs. 0. Please add money to your wallet to respond to booking requests.",
                    walletBalance = provider.WalletBalance
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            bool isCustomerRequest =
                booking.IsRescheduledByCustomer ||
                string.Equals(
                    booking.RescheduleRequestedBy,
                    "Customer",
                    StringComparison.OrdinalIgnoreCase);

            if (!isCustomerRequest ||
                booking.RescheduledStatus != "Pending")
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "No pending customer reschedule request found."
                });
            }

            // Keep the requester identity so the Rejected status is shown
            // only to the opposite side (the customer in this case).
            booking.RequestedDate = null;
            booking.RequestedTime = null;

            booking.IsRescheduledByCustomer = false;
            booking.IsRescheduledByProvider = false;

            booking.RescheduledStatus =
                "Rejected";

            booking.RescheduleRequestedBy = "Customer";

            // Rejecting a reschedule does NOT accept or reject the booking.
            // The original booking status remains unchanged.

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Reschedule Rejected",
                    $"Your reschedule request for booking {booking.BookingId ?? booking.Id.ToString()} has been rejected.",
                    "Reschedule",
                    "fa-solid fa-calendar-xmark",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Customer reschedule request rejected successfully.",

                bookingId = booking.Id,

                bookingStatus =
                    booking.Status,

                rescheduleStatus =
                    booking.RescheduledStatus
            });
        }

        // Old provider respond method

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult>
            ProviderRespondReschedule(
                int jobId,
                bool accept)
        {
            if (accept)
            {
                return await AcceptReschedule(jobId);
            }

            return await RejectReschedule(jobId);
        }

        // Wallet request

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitWalletRequest(
            decimal Amount,
            IFormFile? receiptImage)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Login", "Home");
            }

            if (Amount <= 0)
            {
                TempData["ErrorMessage"] = "Please enter a valid positive amount.";
                return RedirectToAction("Dashboard");
            }

            string? uniqueFileName = null;

            if (receiptImage != null && receiptImage.Length > 0)
            {
                string uploadsFolder = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "wwwroot",
                    "uploads",
                    "receipts");

                Directory.CreateDirectory(uploadsFolder);

                string extension = Path.GetExtension(receiptImage.FileName);
                uniqueFileName = $"{Guid.NewGuid()}{extension}";
                string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                await using (var fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await receiptImage.CopyToAsync(fileStream);
                }
            }

            var walletRequest = new WalletRequest
            {
                ProviderId = !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? (provider.ProviderId.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase)
                        ? provider.ProviderId.Trim()
                        : "PRO-" + provider.ProviderId.Trim())
                    : "PRO-" + provider.Id,
                ProviderName = provider.Name ?? "Provider",
                Amount = Amount,
                ReceiptImagePath = uniqueFileName != null ? "/uploads/receipts/" + uniqueFileName : string.Empty,
                Status = "Pending",
                RequestDate = DateTime.Now
            };

            _context.WalletRequests.Add(walletRequest);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Wallet top-up request submitted successfully!";
            TempData["SuccessMessageProviderId"] =
                !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? (provider.ProviderId.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase)
                        ? provider.ProviderId
                        : "PRO-" + provider.ProviderId)
                    : "PRO-" + provider.Id;
            return RedirectToAction("Dashboard");
        }

        // Accept job

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptJob(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            if (!string.IsNullOrWhiteSpace(booking.Status) &&
                !string.Equals(
                    booking.Status.Trim(),
                    "Pending",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "This booking is no longer pending."
                });
            }

            if (ShouldExpirePendingBooking(
                booking.BookingDate,
                booking.BookingTime))
            {
                booking.Status =
                    "Expired";

                booking.RescheduledStatus =
                    "Expired";

                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message =
                        "This booking has expired and can no longer be accepted."
                });
            }

            // Fixed platform commission: Rs.10 per accepted booking.
            const decimal bookingDeduction = 10m;

            if (provider.WalletBalance < bookingDeduction)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Insufficient wallet balance. Rs.10 is required to accept this booking.",
                    walletBalance = provider.WalletBalance,
                    commission = bookingDeduction
                });
            }

            provider.WalletBalance -= bookingDeduction;

            booking.Status =
                "Accepted";

            booking.RescheduledStatus =
                null;

            booking.RescheduleRequestedBy =
                null;

            booking.IsRescheduledByCustomer =
                false;

            booking.IsRescheduledByProvider =
                false;

            booking.RequestedDate =
                null;

            booking.RequestedTime =
                null;

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Booking Accepted",
                    $"Your booking {booking.BookingId ?? booking.Id.ToString()} has been accepted by {provider.Name ?? "your provider"}.",
                    "Booking",
                    "fa-solid fa-calendar-check",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Booking accepted successfully. Rs. 10 commission deducted from wallet.",

                bookingId =
                    booking.Id,

                commission =
                    bookingDeduction,

                walletBalance =
                    provider.WalletBalance,

                bookingStatus =
                    booking.Status
            });
        }

        // Reject job

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectJob(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            // Zero wallet balance: pending booking requests are locked.
            if (provider.WalletBalance <= 0)
            {
                return StatusCode(403, new
                {
                    success = false,
                    message =
                        "Your wallet balance is Rs. 0. Please add money to your wallet to respond to booking requests.",
                    walletBalance = provider.WalletBalance
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            if (booking.Status != "Pending" &&
                booking.Status != null)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Only pending bookings can be rejected."
                });
            }

            booking.Status =
                "Rejected";

            booking.RejectedAt =
                DateTime.Now;

            booking.RescheduledStatus =
                null;

            booking.RequestedDate =
                null;

            booking.RequestedTime =
                null;

            booking.RescheduleRequestedBy =
                null;

            booking.IsRescheduledByCustomer =
                false;

            booking.IsRescheduledByProvider =
                false;

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Booking Rejected",
                    $"Your booking {booking.BookingId ?? booking.Id.ToString()} was rejected by {provider.Name ?? "the provider"}.",
                    "Booking",
                    "fa-solid fa-calendar-xmark",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Job rejected successfully."
            });
        }

        // Complete job

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteJob(
            int bookingId)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            var booking =
                await FindProviderBookingAsync(
                    bookingId,
                    provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            if (booking.Status != "Accepted")
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Only accepted bookings can be completed."
                });
            }

            if (booking.RescheduledStatus == "Pending")
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Please resolve the pending reschedule request first."
                });
            }

            booking.Status =
                "Completed";

            booking.CompletedAt =
                DateTime.Now;

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Booking Completed",
                    $"Your booking {booking.BookingId ?? booking.Id.ToString()} has been marked completed.",
                    "Booking",
                    "fa-solid fa-circle-check",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("CusDashboard", "Customer", new { bookingId = booking.Id }));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,

                message =
                    "Job completed successfully."
            });
        }

        // Cancel booking

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelBooking(
            int bookingId)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            var booking = await FindProviderBookingAsync(bookingId, provider.Id);

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Booking not found."
                });
            }

            if (string.Equals(
                    booking.Status?.Trim(),
                    "Completed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Completed booking cannot be cancelled."
                });
            }

            if (string.Equals(
                    booking.Status?.Trim(),
                    "Cancelled",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Booking is already cancelled."
                });
            }

            if (!IsCancellationAllowed(booking))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Accepted bookings can only be cancelled before the final two-hour window."
                });
            }

            booking.Status = "Cancelled";
            booking.CancelledAt = DateTime.Now;
            booking.CancelledBy = "Provider";

            booking.RequestedDate = null;
            booking.RequestedTime = null;
            booking.RescheduleRequestedBy = null;
            booking.IsRescheduledByCustomer = false;
            booking.IsRescheduledByProvider = false;
            booking.RescheduledStatus = null;

            var customerEmail = await GetCustomerEmailAsync(booking.CustomerID);
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                AddNotification(
                    customerEmail,
                    "Booking Cancelled",
                    $"Booking {booking.BookingId ?? booking.Id.ToString()} was cancelled by {provider.Name ?? "the provider"}.",
                    "Cancellation",
                    "fa-solid fa-calendar-xmark",
                    provider.Id.ToString(),
                    booking.Id,
                    booking.BookingId,
                    Url.Action("History", "Customer"));
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = "Booking cancelled successfully.",
                walletBalance = provider.WalletBalance,
                bookingStatus = booking.Status
            });
        }

        // Profile GET

        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            // Reload-safe provider lookup for Profile.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Login", "Home");
            }

            ViewBag.ProviderProfileImage = provider.ProfilePicPath;
            ViewBag.UnreadCount = await GetProviderUnreadNotificationCountAsync(provider);

            var model =
                new ProviderProfile
                {
                    Id =
                        provider.Id,

                    ProviderId =
                        provider.ProviderId,

                    Name =
                        provider.Name,

                    FullName =
                        provider.Name,

                    FatherName =
                        provider.FatherName,

                    Email =
                        provider.Email,

                    CNIC =
                        provider.CNIC,

                    PhoneNumber =
                        provider.PhoneNumber,

                    Phone =
                        provider.PhoneNumber,

                    City =
                        provider.City,

                    LocalAddress =
                        provider.LocalAddress,

                    PermanentAddress =
                        provider.PermanentAddress,

                    Location =
                        provider.LocalAddress ??
                        provider.City,

                    SelectedServices =
                        provider.SelectedServices,

                    SelectedServiceTypes =
                        provider.SelectedServiceTypes,

                    ShopAddressInput =
                        provider.ShopAddressInput,

                    ShopLat =
                        provider.ShopLat,

                    ShopLng =
                        provider.ShopLng,

                    HomeAddressInput =
                        provider.HomeAddressInput,

                    HomeLat =
                        provider.HomeLat,

                    HomeLng =
                        provider.HomeLng,

                    AvailableDays =
                        provider.AvailableDays ??
                        string.Empty,

                    StartTime =
                        provider.StartTime,

                    EndTime =
                        provider.EndTime,

                    ProfilePicPath =
                        provider.ProfilePicPath,

                    CNICFrontPath =
                        provider.CNICFrontPath,

                    CNICBackPath =
                        provider.CNICBackPath,

                    CertificatePath =
                        provider.CertificatePath,

                    AffidavitPath =
                        provider.AffidavitPath,

                    VideoPath =
                        provider.VideoPath,

                    Status =
                        provider.Status,

                    Rating =
                        provider.Rating,

                    ReviewsCount =
                        provider.ReviewsCount,

                    WalletBalance =
                        provider.WalletBalance
                };

            return View(model);
        }

        // Profile POST

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(
            ProviderProfile model)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                if (Request.Headers["X-Requested-With"]
                    == "XMLHttpRequest")
                {
                    return Json(new
                    {
                        success = false,
                        message =
                            "Session expired. Please login again."
                    });
                }

                return RedirectToAction(
                    "Login",
                    "Home");
            }

            provider.Name = model.Name;
            provider.FatherName = model.FatherName;
            provider.CNIC = model.CNIC;
            provider.PhoneNumber = model.PhoneNumber;
            provider.City = model.City;
            provider.LocalAddress = model.LocalAddress;
            provider.PermanentAddress = model.PermanentAddress;
            provider.SelectedServices = model.SelectedServices;
            provider.SelectedServiceTypes = model.SelectedServiceTypes;

            provider.ShopAddressInput = model.ShopAddressInput;
            provider.ShopLat = model.ShopLat;
            provider.ShopLng = model.ShopLng;

            provider.HomeAddressInput = model.HomeAddressInput;
            provider.HomeLat = model.HomeLat;
            provider.HomeLng = model.HomeLng;

            provider.AvailableDays = model.AvailableDays;
            provider.StartTime = model.StartTime;
            provider.EndTime = model.EndTime;

            // Profile picture is a normal profile asset and may be changed.
            if (model.ProfileInput != null &&
                model.ProfileInput.Length > 0)
            {
                string uploadsFolder = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "wwwroot",
                    "uploads");

                Directory.CreateDirectory(uploadsFolder);

                string extension =
                    Path.GetExtension(model.ProfileInput.FileName);

                string uniqueFileName =
                    $"{Guid.NewGuid()}{extension}";

                string filePath =
                    Path.Combine(uploadsFolder, uniqueFileName);

                await using var fileStream =
                    new FileStream(filePath, FileMode.Create);

                await model.ProfileInput.CopyToAsync(fileStream);

                provider.ProfilePicPath =
                    "/uploads/" + uniqueFileName;
            }

            // CNIC front/back, certificate, affidavit and verification video
            // are approved verification documents. They are intentionally NOT
            // updated from the edit-profile request after verification.
            await _context.SaveChangesAsync();

            // The profile form is a normal multipart form submission.
            // Do not return JSON for multipart/form-data, otherwise the browser
            // displays the JSON response instead of returning to the Profile page.
            if (Request.Headers["X-Requested-With"]
                    == "XMLHttpRequest")
            {
                return Json(new
                {
                    success = true,
                    message = "Your profile changes have been saved successfully."
                });
            }

            TempData["SuccessMessage"] =
                "Your profile changes have been saved successfully.";
            TempData["SuccessMessageProviderId"] =
                !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? (provider.ProviderId.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase)
                        ? provider.ProviderId
                        : "PRO-" + provider.ProviderId)
                    : "PRO-" + provider.Id;

            return RedirectToAction("Profile");
        }

        // Provider history

        [HttpGet]
        public async Task<IActionResult> History()
        {
            // Use the same reload-safe provider lookup as Dashboard/Notifications.
            // This prevents a normal page refresh from falling back to Login when
            // session data is temporarily unavailable but the auth identity exists.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Login", "Home");
            }

            ViewBag.UnreadCount = await GetProviderUnreadNotificationCountAsync(provider);

            var bookings = await _context.Booking
                .AsNoTracking()
                .Where(b =>
                    b.ProviderId == provider.Id &&
                    (
                        // Job History: only Completed, Cancelled and Rejected.
                        b.Status == "Completed" ||
                        b.Status == "Cancelled" ||
                        b.Status == "Rejected"
                    ))
                .OrderByDescending(b => b.Id)
                .ToListAsync();

            var customerCodes = bookings
                .Where(b => !string.IsNullOrWhiteSpace(b.CustomerID))
                .Select(b => b.CustomerID!)
                .Distinct()
                .ToList();

            var customerNames =
                new Dictionary<string, string>();

            if (customerCodes.Count > 0)
            {
                var customers = await _context.CustomerSignup
                    .AsNoTracking()
                    .Where(c =>
                        c.CustomerId != null &&
                        customerCodes.Contains(c.CustomerId))
                    .Select(c => new
                    {
                        c.CustomerId,
                        c.Name
                    })
                    .ToListAsync();

                customerNames = customers
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(
                            x.CustomerId))
                    .GroupBy(x => x.CustomerId!)
                    .ToDictionary(
                        x => x.Key,
                        x => x.First().Name ?? "Customer"
                    );
            }

            var jobHistory = bookings
                .Select(b =>
                {
                    string customerName =
                        b.CustomerName;

                    if (string.IsNullOrWhiteSpace(customerName) &&
                        !string.IsNullOrWhiteSpace(b.CustomerID))
                    {
                        if (customerNames.TryGetValue(
                            b.CustomerID,
                            out var foundName))
                        {
                            customerName = foundName;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(customerName))
                    {
                        customerName = "Customer";
                    }

                    bool hasRescheduleHistory =
                        !string.IsNullOrWhiteSpace(
                            b.RescheduleRequestedBy)
                        ||
                        b.RequestedDate.HasValue
                        ||
                        !string.IsNullOrWhiteSpace(
                            b.RequestedTime)
                        ||
                        b.RescheduledDate.HasValue
                        ||
                        !string.IsNullOrWhiteSpace(
                            b.RescheduledTime)
                        ||
                        !string.IsNullOrWhiteSpace(
                            b.RescheduledStatus)
                        ||
                        b.IsRescheduledByCustomer
                        ||
                        b.IsRescheduledByProvider;

                    string? requestedBy =
                        b.RescheduleRequestedBy;

                    if (string.IsNullOrWhiteSpace(requestedBy))
                    {
                        if (b.IsRescheduledByProvider)
                        {
                            requestedBy = "Provider";
                        }
                        else if (b.IsRescheduledByCustomer)
                        {
                            requestedBy = "Customer";
                        }
                    }

                    return new ProviderJobHistoryItem
                    {
                        Id = b.Id,

                        BookingId =
                            string.IsNullOrWhiteSpace(
                                b.BookingId)
                                ? b.Id.ToString()
                                : b.BookingId,

                        CustomerId =
                            b.CustomerID ?? string.Empty,

                        CustomerName =
                            customerName,

                        ServiceName =
                            b.ServiceName ?? string.Empty,

                        ServiceType =
                            b.ServiceType ?? string.Empty,

                        ProblemDetails =
                            b.ProblemDetails ?? string.Empty,

                        Address =
                            b.Address ?? string.Empty,

                        BookingDate =
                            b.BookingDate,

                        BookingTime =
                            b.BookingTime ?? string.Empty,

                        Status =
                            string.IsNullOrWhiteSpace(
                                b.Status)
                                ? "Unknown"
                                : b.Status,

                        CompletedAt =
                            b.CompletedAt,

                        CancelledAt =
                            b.CancelledAt,

                        CancelledBy =
                            b.CancelledBy,

                        RejectedAt =
                            b.RejectedAt,

                        HasRescheduleHistory =
                            hasRescheduleHistory,

                        RescheduleRequestedBy =
                            requestedBy,

                        RequestedDate =
                            b.RequestedDate,

                        RequestedTime =
                            b.RequestedTime,

                        RescheduledDate =
                            b.RescheduledDate,

                        RescheduledTime =
                            b.RescheduledTime,

                        RescheduledStatus =
                            b.RescheduledStatus,

                        VisitCharges =
                            b.VisitCharges,

                    };
                })
                .ToList();

            var complaintHistory =
                await _context.Complaints
                    .AsNoTracking()
                    .Where(c =>
                        c.ProviderId ==
                        provider.Id.ToString())
                    .OrderByDescending(c => c.CreatedAt)
                    .ToListAsync();

            ViewBag.ProviderProfileImage = provider.ProfilePicPath;

            var model = new ProviderHistoryViewModel
            {
                ProviderName =
                    provider.Name ?? "Provider",

                ProviderId =
                    !string.IsNullOrWhiteSpace(
                        provider.ProviderId)
                        ? provider.ProviderId
                        : "PRO-" + provider.Id,

                JobHistory =
                    jobHistory,

                ComplaintHistory =
                    complaintHistory
            };

            return View(model);
        }

        // Messages page

        [HttpGet]
        public async Task<IActionResult> Messages(
            string? customerCode,
            string? customerName)
        {
            // Reload-safe lookup: do not rely on session alone for opening Messages.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Login", "Home");
            }

            ViewBag.ProviderProfileImage = provider.ProfilePicPath;
            ViewBag.UnreadCount = await GetProviderUnreadNotificationCountAsync(provider);

            string providerId =
                provider.Id.ToString();

            string providerCode =
                !string.IsNullOrWhiteSpace(
                    provider.ProviderId)
                    ? provider.ProviderId
                    : "PRO-" + provider.Id;

            var selectedCustomer =
                await ResolveCustomerForMessagingAsync(customerCode);

            var selectedCustomerIds =
                selectedCustomer != null
                    ? GetCustomerMessageIds(selectedCustomer)
                    : new List<string>();

            string selectedCustomerId =
                selectedCustomer != null &&
                !string.IsNullOrWhiteSpace(selectedCustomer.CustomerId)
                    ? selectedCustomer.CustomerId.Trim()
                    : (selectedCustomer != null
                        ? selectedCustomer.Id.ToString()
                        : (customerCode ?? string.Empty).Trim());

            var allMessages =
                await _context.Messages
                    .Where(m =>
                        m.SenderId == providerId ||
                        m.ReceiverId == providerId ||
                        m.SenderId == providerCode ||
                        m.ReceiverId == providerCode)
                    .OrderBy(m => m.Timestamp)
                    .ToListAsync();

            // Build the chat list from customer accounts, not from raw message IDs.
            // A single customer can appear in Messages as CustomerId, numeric Id,
            // CUS-Id or CUSId. Those aliases must still produce ONE chat.
            var rawCustomerCodes =
                allMessages
                    .Select(m =>
                        m.SenderId == providerId ||
                        m.SenderId == providerCode
                            ? m.ReceiverId
                            : m.SenderId)
                    .Where(id =>
                        !string.IsNullOrWhiteSpace(id) &&
                        id != providerId &&
                        id != providerCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (selectedCustomerIds.Count > 0)
            {
                foreach (var id in selectedCustomerIds)
                {
                    if (!rawCustomerCodes.Contains(id, StringComparer.OrdinalIgnoreCase))
                        rawCustomerCodes.Add(id);
                }
            }
            else if (!string.IsNullOrWhiteSpace(customerCode) &&
                     !rawCustomerCodes.Contains(customerCode, StringComparer.OrdinalIgnoreCase))
            {
                rawCustomerCodes.Add(customerCode.Trim());
            }

            // Resolve every raw ID to one canonical CustomerId/DB Id.
            // This is what prevents duplicate chat rows for the same customer.
            var customerCodes = new List<string>();

            foreach (var rawCode in rawCustomerCodes)
            {
                var resolvedCustomer =
                    await ResolveCustomerForMessagingAsync(rawCode);

                var canonicalCode =
                    resolvedCustomer != null &&
                    !string.IsNullOrWhiteSpace(resolvedCustomer.CustomerId)
                        ? resolvedCustomer.CustomerId.Trim()
                        : resolvedCustomer != null
                            ? resolvedCustomer.Id.ToString()
                            : rawCode.Trim();

                if (!customerCodes.Contains(canonicalCode, StringComparer.OrdinalIgnoreCase))
                {
                    customerCodes.Add(canonicalCode);
                }
            }

            var chatsData =
                new List<object>();

            foreach (var code in customerCodes)
            {
                string name =
                    "Customer";

                // Resolve customer code in all formats used by ServiceGo:
                // CustomerId, numeric DB Id, CUS-123 and CUS123.
                var cleanCustomerCode = (code ?? string.Empty).Trim();
                var numericCustomerCode = cleanCustomerCode;

                if (numericCustomerCode.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                {
                    numericCustomerCode = numericCustomerCode.Substring(4).Trim();
                }
                else if (numericCustomerCode.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                {
                    numericCustomerCode = numericCustomerCode.Substring(3).Trim();
                }

                int customerDbId;
                bool hasNumericCustomerId =
                    int.TryParse(numericCustomerCode, out customerDbId);

                var customer =
                    await _context.CustomerSignup
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c =>
                            (!string.IsNullOrWhiteSpace(c.CustomerId) &&
                             c.CustomerId.Trim() == cleanCustomerCode) ||
                            (hasNumericCustomerId && c.Id == customerDbId));

                if (customer != null)
                {
                    name =
                        customer.Name ?? "Customer";
                }
                else
                {
                    var register =
                        await _context.Register
                            .FirstOrDefaultAsync(
                                r =>
                                    r.Id.ToString() == code ||
                                    r.ProviderId == code);

                    if (register != null)
                    {
                        name =
                            register.Name;
                    }
                    else if (
                        code == customerCode &&
                        !string.IsNullOrWhiteSpace(
                            customerName))
                    {
                        name =
                            Uri.UnescapeDataString(
                                customerName);
                    }
                }

                var customerProfileImage = customer?.ProfileImage;

                chatsData.Add(new
                {
                    CustomerId =
                        code,

                    CustomerName =
                        name,

                    CustomerProfileImage =
                        customerProfileImage
                });
            }

            ViewBag.Chats =
                chatsData;

            ViewBag.ActiveCustomerCode =
                selectedCustomerId;

            string activeCustomerName =
                "Select a chat";

            List<Message> activeMessages =
                new List<Message>();

            if (!string.IsNullOrWhiteSpace(
                customerCode))
            {
                activeMessages =
                    allMessages
                        .Where(m =>
                            (
                                (
                                    m.SenderId == providerId ||
                                    m.SenderId == providerCode
                                ) &&
                                selectedCustomerIds.Contains(
                                    m.ReceiverId ?? string.Empty,
                                    StringComparer.OrdinalIgnoreCase)
                            )
                            ||
                            (
                                selectedCustomerIds.Contains(
                                    m.SenderId ?? string.Empty,
                                    StringComparer.OrdinalIgnoreCase) &&
                                (
                                    m.ReceiverId == providerId ||
                                    m.ReceiverId == providerCode
                                )
                            ))
                        .OrderBy(m => m.Timestamp)
                        .ToList();

                activeMessages =
                    activeMessages
                        .Where(m => !m.DeletedForEveryone &&
                            ((m.SenderId == providerId || m.SenderId == providerCode)
                                ? !m.DeletedForSender
                                : !m.DeletedForReceiver))
                        .ToList();

                var activeChat =
                    chatsData
                        .Cast<dynamic>()
                        .FirstOrDefault(c =>
                            string.Equals(
                                c.CustomerId,
                                selectedCustomerId,
                                StringComparison.OrdinalIgnoreCase) ||
                            selectedCustomerIds.Contains(
                                Convert.ToString((object)c.CustomerId),
                                StringComparer.OrdinalIgnoreCase));

                if (activeChat != null)
                {
                    activeCustomerName =
                        activeChat.CustomerName;
                }
                else if (
                    !string.IsNullOrWhiteSpace(
                        customerName))
                {
                    activeCustomerName =
                        Uri.UnescapeDataString(
                            customerName);
                }
            }

            ViewBag.ActiveCustomerName =
                activeCustomerName;

            // Resolve the selected customer's current profile image directly
            // so the chat header can always use the same source as the chat list.
            string activeCustomerProfileImage = string.Empty;
            if (!string.IsNullOrWhiteSpace(customerCode))
            {
                string activeCode = customerCode.Trim();
                string activeNumericCode = activeCode;

                if (activeNumericCode.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                    activeNumericCode = activeNumericCode.Substring(4).Trim();
                else if (activeNumericCode.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                    activeNumericCode = activeNumericCode.Substring(3).Trim();

                if (int.TryParse(activeNumericCode, out int activeCustomerDbId))
                {
                    activeCustomerProfileImage = await _context.CustomerSignup
                        .AsNoTracking()
                        .Where(c => c.Id == activeCustomerDbId)
                        .Select(c => c.ProfileImage ?? string.Empty)
                        .FirstOrDefaultAsync() ?? string.Empty;
                }
                else
                {
                    activeCustomerProfileImage = await _context.CustomerSignup
                        .AsNoTracking()
                        .Where(c => c.CustomerId == activeCode)
                        .Select(c => c.ProfileImage ?? string.Empty)
                        .FirstOrDefaultAsync() ?? string.Empty;
                }
            }

            ViewBag.ActiveCustomerProfileImage = activeCustomerProfileImage;
            ViewBag.WalletLocked = false;

            return View(activeMessages);
        }

        // Send text message

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SendMessage(
            string? customerCode,
            string? messageText)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    status = "error",
                    message = "Session expired. Please login again."
                });
            }

            if (string.IsNullOrWhiteSpace(customerCode))
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer is not selected."
                });
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Message cannot be empty."
                });
            }

            var messageCustomer =
                await ResolveCustomerForMessagingAsync(customerCode);

            if (messageCustomer == null)
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer account could not be resolved."
                });
            }

            // Customer side reads messages using the exact
            // CustomerSignup.CustomerId value. Do not trim/change it here,
            // otherwise provider->customer messages can be stored under a
            // different identifier and disappear when the customer reopens chat.
            var cleanCustomerCode =
                !string.IsNullOrWhiteSpace(messageCustomer.CustomerId)
                    ? messageCustomer.CustomerId
                    : messageCustomer.Id.ToString();

            var text =
                messageText.Trim();

            var newMessage =
                new Message
                {
                    SenderId =
                        provider.Id.ToString(),

                    ReceiverId =
                        cleanCustomerCode,

                    SenderName =
                        !string.IsNullOrWhiteSpace(provider.Name)
                            ? provider.Name
                            : provider.Email,

                    TextContent =
                        text,

                    IsFromCustomer =
                        false,

                    Timestamp =
                        DateTime.Now
                };

            _context.Messages.Add(newMessage);

            await _context.SaveChangesAsync();

            // Notify ONLY the customer who received this message.
            // The provider who sent the message must never receive his own notification.
            // (Customer and provider are different people even if their numeric
            // ids match, e.g. customer 1 and provider 1, so ids are NOT compared.)
            if (messageCustomer != null)
            {
                AddNotification(
                    messageCustomer.Email ?? messageCustomer.CustomerId ?? messageCustomer.Id.ToString(),
                    "New Message",
                    $"{provider.Name ?? "Provider"} sent you a new message.",
                    "Message",
                    "fa-solid fa-message",
                    provider.Id.ToString(),
                    null,
                    newMessage.MessageId.ToString(),
                    Url.Action("Messages", "Customer", new { providerId = provider.Id }));

                await _context.SaveChangesAsync();
            }

            return Json(new
            {
                status = "success",

                id = newMessage.MessageId,

                text = text,

                time =
                    newMessage.Timestamp
                        .ToString("hh:mm tt")
            });
        }

        // Send audio message

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SendAudioMessage(
            string? customerCode,
            IFormFile? audioFile)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    status = "error",
                    message = "Session expired. Please login again."
                });
            }

            if (string.IsNullOrWhiteSpace(customerCode))
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer is not selected."
                });
            }

            if (audioFile == null ||
                audioFile.Length == 0)
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Audio file is empty."
                });
            }

            string uploadsFolder =
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "wwwroot",
                    "uploads");

            Directory.CreateDirectory(
                uploadsFolder);

            string extension =
                Path.GetExtension(
                    audioFile.FileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".webm";
            }

            string uniqueFileName =
                $"{Guid.NewGuid()}{extension}";

            string filePath =
                Path.Combine(
                    uploadsFolder,
                    uniqueFileName);

            await using (var stream =
                new FileStream(
                    filePath,
                    FileMode.Create))
            {
                await audioFile.CopyToAsync(stream);
            }

            string relativeUrl =
                "/uploads/" +
                uniqueFileName;

            var audioCustomer =
                await ResolveCustomerForMessagingAsync(customerCode);

            if (audioCustomer == null)
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer account could not be resolved."
                });
            }

            // Keep the exact CustomerSignup.CustomerId for the same reason as text messages.
            var canonicalAudioCustomerCode =
                !string.IsNullOrWhiteSpace(audioCustomer.CustomerId)
                    ? audioCustomer.CustomerId
                    : audioCustomer.Id.ToString();

            var audioMessage =
                new Message
                {
                    SenderId =
                        provider.Id.ToString(),

                    ReceiverId =
                        canonicalAudioCustomerCode,

                    SenderName =
                        !string.IsNullOrWhiteSpace(provider.Name)
                            ? provider.Name
                            : provider.Email,

                    TextContent =
                        relativeUrl,

                    AudioUrl =
                        relativeUrl,

                    IsFromCustomer =
                        false,

                    Timestamp =
                        DateTime.Now
                };

            _context.Messages.Add(
                audioMessage);

            await _context.SaveChangesAsync();

            // Notify ONLY the customer who received this voice message.
            if (audioCustomer != null)
            {
                AddNotification(
                    audioCustomer.Email ?? audioCustomer.CustomerId ?? audioCustomer.Id.ToString(),
                    "New Voice Message",
                    $"{provider.Name ?? "Provider"} sent you a voice message.",
                    "Message",
                    "fa-solid fa-microphone",
                    provider.Id.ToString(),
                    null,
                    audioMessage.MessageId.ToString(),
                    Url.Action("Messages", "Customer", new { providerId = provider.Id }));

                await _context.SaveChangesAsync();
            }

            return Json(new
            {
                status = "success",

                id =
                    audioMessage.MessageId,

                audioUrl =
                    relativeUrl,

                time =
                    audioMessage.Timestamp
                        .ToString("hh:mm tt")
            });
        }

        // Delete chat

        // Delete chat

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteChat(
            string? customerCode)
        {
            var provider =
                await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    status = "error",
                    message = "Session expired. Please login again."
                });
            }

            if (string.IsNullOrWhiteSpace(customerCode))
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer is not selected."
                });
            }

            var chatCustomer =
                await ResolveCustomerForMessagingAsync(customerCode);

            if (chatCustomer == null)
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Customer account could not be resolved."
                });
            }

            var chatCustomerIds =
                GetCustomerMessageIds(chatCustomer);

            string cleanCustomerCode =
                !string.IsNullOrWhiteSpace(chatCustomer.CustomerId)
                    ? chatCustomer.CustomerId.Trim()
                    : chatCustomer.Id.ToString();

            string providerId =
                provider.Id.ToString();

            string providerCode =
                !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? provider.ProviderId
                    : "PRO-" + provider.Id;

            // EF Core cannot reliably translate List.Contains(value, comparer)
            // to SQL. Load only this provider's messages first, then match the
            // customer's aliases in memory. This also deletes old chats saved
            // with any of the supported customer ID formats.
            var providerMessages =
                await _context.Messages
                    .Where(m =>
                        m.SenderId == providerId ||
                        m.ReceiverId == providerId ||
                        m.SenderId == providerCode ||
                        m.ReceiverId == providerCode)
                    .ToListAsync();

            var customerIdSet =
                new HashSet<string>(
                    chatCustomerIds,
                    StringComparer.OrdinalIgnoreCase);

            var chatMessages =
                providerMessages
                    .Where(m =>
                        (
                            (
                                m.SenderId == providerId ||
                                m.SenderId == providerCode
                            ) &&
                            !string.IsNullOrWhiteSpace(m.ReceiverId) &&
                            customerIdSet.Contains(m.ReceiverId)
                        )
                        ||
                        (
                            !string.IsNullOrWhiteSpace(m.SenderId) &&
                            customerIdSet.Contains(m.SenderId) &&
                            (
                                m.ReceiverId == providerId ||
                                m.ReceiverId == providerCode
                            )
                        ))
                    .ToList();

            if (chatMessages.Count == 0)
            {
                return Json(new
                {
                    status = "success",
                    deletedCount = 0,
                    customerCode = cleanCustomerCode
                });
            }

            // Delete audio files belonging to this chat
            foreach (var message in chatMessages)
            {
                if (!string.IsNullOrWhiteSpace(message.AudioUrl))
                {
                    DeleteMessageAudioFile(message.AudioUrl);
                }
            }

            // Permanently delete the whole chat
            _context.Messages.RemoveRange(chatMessages);

            await _context.SaveChangesAsync();

            return Json(new
            {
                status = "success",
                deletedCount = chatMessages.Count,
                customerCode = cleanCustomerCode
            });
        }

        // Delete message

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteMessage(int messageId, string deleteType)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return Unauthorized(new { status = "error", message = "Session expired. Please login again." });

            string providerId = provider.Id.ToString();
            string providerCode = !string.IsNullOrWhiteSpace(provider.ProviderId) ? provider.ProviderId : "PRO-" + provider.Id;

            deleteType = (deleteType ?? string.Empty).Trim().ToLowerInvariant();

            if (deleteType != "me" && deleteType != "everyone")
                return BadRequest(new { status = "error", message = "Invalid delete option." });

            var message = await _context.Messages.FirstOrDefaultAsync(m =>
                m.MessageId == messageId &&
                (m.SenderId == providerId || m.SenderId == providerCode || m.ReceiverId == providerId || m.ReceiverId == providerCode));

            if (message == null)
                return NotFound(new { status = "error", message = "Message not found or you are not allowed to delete it." });

            bool isSender = message.SenderId == providerId || message.SenderId == providerCode;

            if (deleteType == "everyone")
            {
                if (!isSender)
                    return BadRequest(new { status = "error", message = "Only the sender can delete this message for everyone." });

                if (message.DeletedForEveryone)
                    return Json(new { status = "success", deleteType = "everyone", alreadyDeleted = true, messageId = message.MessageId });

                message.DeletedForEveryone = true;
                message.DeletedForSender = true;
                message.DeletedForReceiver = true;
            }
            else if (isSender)
            {
                if (message.DeletedForSender)
                    return Json(new { status = "success", deleteType = "me", alreadyDeleted = true, messageId = message.MessageId });

                message.DeletedForSender = true;
            }
            else
            {
                if (message.DeletedForReceiver)
                    return Json(new { status = "success", deleteType = "me", alreadyDeleted = true, messageId = message.MessageId });

                message.DeletedForReceiver = true;
            }

            string? audioUrlToDelete = message.DeletedForEveryone ? message.AudioUrl : null;

            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(audioUrlToDelete))
                DeleteMessageAudioFile(audioUrlToDelete);

            return Json(new { status = "success", deleteType = deleteType, messageId = message.MessageId });
        }

        private void DeleteMessageAudioFile(string? audioUrl)
        {
            if (string.IsNullOrWhiteSpace(audioUrl)) return;

            var relativePath = audioUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            var physicalPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "wwwroot",
                relativePath);

            if (System.IO.File.Exists(physicalPath))
            {
                try
                {
                    System.IO.File.Delete(physicalPath);
                }
                catch
                {
                }
            }
        }

        // Complaint GET

        [HttpGet]
        public async Task<IActionResult> Complaint(
            string? customerId,
            string? customerName,
            string? customerEmail)
        {
            // Reload-safe provider lookup for Complaint page.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Login", "Home");
            }

            ViewBag.ProviderId =
                provider.Id.ToString();

            ViewBag.UnreadCount =
                await GetProviderUnreadNotificationCountAsync(provider);

            ViewBag.ProviderName =
                provider.Name;

            ViewBag.CustomerId =
                customerId;

            ViewBag.CustomerName =
                customerName;

            ViewBag.CustomerEmail =
                customerEmail;

            var lastComplaint =
                await _context.Complaints
                    .OrderByDescending(c => c.Id)
                    .FirstOrDefaultAsync();

            int nextId =
                lastComplaint != null
                    ? lastComplaint.Id + 1
                    : 1;

            ViewBag.NextComplaintId =
                "COMP-" + nextId;

            return View();
        }

        // Complaint POST

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Complaint(
            ComplaintModel model)
        {
            bool isAjax = Request.Headers["X-Requested-With"]
                .ToString()
                .Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                if (isAjax)
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message = "Your session has expired. Please login again."
                    });
                }

                return RedirectToAction("Login", "Home");
            }

            // One complaint per booking for this provider.
            var submittedComplaintBookingId = Request.Form["BookingId"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(submittedComplaintBookingId))
            {
                var bookingIdProperty = typeof(ComplaintModel).GetProperty("BookingId");
                if (bookingIdProperty != null)
                {
                    var providerComplaints = await _context.Complaints
                        .AsNoTracking()
                        .Where(c => c.ProviderId == provider.Id.ToString())
                        .ToListAsync();

                    bool duplicateComplaint = providerComplaints.Any(c =>
                        string.Equals(
                            Convert.ToString(bookingIdProperty.GetValue(c)),
                            submittedComplaintBookingId,
                            StringComparison.OrdinalIgnoreCase));

                    if (duplicateComplaint)
                    {
                        if (isAjax)
                            return Conflict(new { success = false, message = "You can file only one complaint for this booking." });

                        TempData["ErrorMessage"] = "You can file only one complaint for this booking.";
                        return RedirectToAction("Dashboard");
                    }
                }
            }

            // Complaints are allowed only for a completed booking owned by the logged-in user.
            if (int.TryParse(submittedComplaintBookingId, out int complaintBookingDbId))
            {
                var complaintBooking = await _context.Booking
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b =>
                        b.Id == complaintBookingDbId &&
                        b.ProviderId == provider.Id);

                if (complaintBooking == null ||
                    !string.Equals(complaintBooking.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (isAjax)
                        return BadRequest(new { success = false, message = "A complaint can only be filed for a completed booking." });

                    TempData["ErrorMessage"] = "A complaint can only be filed for a completed booking.";
                    return RedirectToAction("Dashboard");
                }
            }
            else
            {
                if (isAjax)
                    return BadRequest(new { success = false, message = "Valid booking ID is required for a complaint." });

                TempData["ErrorMessage"] = "Valid booking ID is required for a complaint.";
                return RedirectToAction("Dashboard");
            }

            model.ProviderId =
                provider.Id.ToString();

            model.ProviderName =
                provider.Name ??
                "Provider";

            if (string.IsNullOrWhiteSpace(
                model.CustomerEmail) &&
                !string.IsNullOrWhiteSpace(
                    model.CustomerId))
            {
                var customerCode =
                    model.CustomerId.Trim();

                var customer =
                    await _context.CustomerSignup
                        .FirstOrDefaultAsync(
                            c =>
                                c.CustomerId ==
                                    customerCode ||
                                c.Id.ToString() ==
                                    customerCode);

                if (customer != null)
                {
                    model.CustomerEmail =
                                customer.Email ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(
                        model.CustomerName))
                    {
                        model.CustomerName =
                            customer.Name ?? "Customer";
                    }
                }
                else
                {
                    var registerCustomer =
                        await _context.Register
                            .FirstOrDefaultAsync(
                                r =>
                                    r.Id.ToString() ==
                                    customerCode);

                    if (registerCustomer != null)
                    {
                        model.CustomerEmail =
                            registerCustomer.Email ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(
                            model.CustomerName))
                        {
                            model.CustomerName =
                                registerCustomer.Name ?? "Customer";
                        }
                    }
                }
            }

            model.CustomerEmail ??=
                "unknown@customer.com";

            model.CustomerName ??=
                "Unknown Customer";

            model.IssueCategory ??=
                "General";

            model.OtherIssue ??=
                string.Empty;

            model.ComplaintDetails ??=
                "No details provided.";

            var lastComplaint =
                await _context.Complaints
                    .OrderByDescending(c => c.Id)
                    .FirstOrDefaultAsync();

            int nextId =
                lastComplaint != null
                    ? lastComplaint.Id + 1
                    : 1;

            model.ComplaintId =
                "COMP-" + nextId;

            model.CreatedAt =
                DateTime.Now;

            try
            {
                _context.Complaints.Add(model);
                await _context.SaveChangesAsync();

                // Provider complaint -> notify ONLY the reported customer.
                // The provider who submitted the complaint must never receive
                // the complaint notification himself.
                if (!string.IsNullOrWhiteSpace(model.CustomerEmail))
                {
                    var reportedCustomer = await _context.CustomerSignup
                        .FirstOrDefaultAsync(c =>
                            c.Email != null &&
                            c.Email != null && c.Email.Trim().ToLower() == model.CustomerEmail.Trim().ToLower());

                    if (reportedCustomer != null &&
                        !string.Equals(
                            reportedCustomer.Email?.Trim(),
                            provider.Email?.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        AddNotification(
                            reportedCustomer.Id.ToString(),
                            "Complaint Received",
                            $"A complaint has been submitted concerning your account. Complaint ID: {model.ComplaintId}.",
                            "Complaint",
                            "fa-solid fa-triangle-exclamation",
                            provider.Id.ToString(),
                            null,
                            model.ComplaintId,
                            Url.Action("Notifications", "Customer"));

                        await _context.SaveChangesAsync();
                    }
                }
                // Complaint must also reach Admin.
                // The provider does not receive a notification for his own complaint.
                _context.Notifications.Add(new Notification
                {
                    UserId = "Admin",
                    TargetUserId = "Admin",
                    SenderUserId = provider.Id.ToString(),
                    Title = "New Provider Complaint",
                    Message = $"Provider {provider.Name ?? "Provider"} has submitted complaint {model.ComplaintId} against a customer.",
                    NotificationType = "Complaint",
                    IconClass = "fa-solid fa-triangle-exclamation",
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    ReferenceId = model.ComplaintId,
                    ActionUrl = Url.Action("Complaint", "Admin")
                });

            }
            catch (DbUpdateException ex)
            {
                var errorMessage = ex.InnerException?.Message ?? ex.Message;

                if (isAjax)
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = "Complaint database mein save nahi ho saki: " + errorMessage
                    });
                }

                TempData["ErrorMessage"] =
                    "Complaint save nahi ho saki: " + errorMessage;

                return RedirectToAction("Dashboard");
            }
            catch (Exception ex)
            {
                if (isAjax)
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = "Complaint submit karte waqt error aa gaya: " + ex.Message
                    });
                }

                TempData["ErrorMessage"] =
                    "Complaint submit karte waqt error aa gaya: " + ex.Message;

                return RedirectToAction("Dashboard");
            }

            if (isAjax)
            {
                return Ok(new
                {
                    success = true,
                    message = "Your complaint has been successfully sent to the admin!",
                    complaintId = model.ComplaintId,
                    customerId = model.CustomerId,
                    customerName = model.CustomerName,
                    createdAt = model.CreatedAt.ToString("dd MMM yyyy hh:mm tt")
                });
            }

            TempData["SuccessMessage"] =
                "Your complaint has been successfully sent to the admin!";
            TempData["SuccessMessageProviderId"] =
                !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? (provider.ProviderId.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase)
                        ? provider.ProviderId
                        : "PRO-" + provider.ProviderId)
                    : "PRO-" + provider.Id;

            return RedirectToAction(
                "Complaint",
                new
                {
                    customerId = model.CustomerId,
                    customerName = model.CustomerName,
                    customerEmail = model.CustomerEmail
                });
        }

        // Logout

        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            // Keep the confirmation popup available even when ProviderId
            // session value is not present, as long as the provider can be
            // identified through the normal provider lookup.
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return RedirectToAction("Index", "Home");
            }

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> KeepAlive()
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Session expired."
                });
            }

            // Touch the session so its idle timeout is refreshed while
            // the provider dashboard remains open.
            HttpContext.Session.SetString(
                "UserEmail",
                provider.Email ?? string.Empty);

            HttpContext.Session.SetString(
                "UserRole",
                "Provider");

            HttpContext.Session.SetInt32(
                "UserId",
                provider.Id);

            return Ok(new
            {
                success = true
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult LogoutConfirmed()
        {
            // Actual logout happens only after the user presses "Yes Logout".
            HttpContext.Session.Clear();

            Response.Cookies.Delete("ServiceGoProviderSession");

            return RedirectToAction("Index", "Home");
        }

        // Provider notification click policy
        private static bool IsProviderNotificationClickable(Notification n)
        {
            var type = (n.NotificationType ?? string.Empty).Trim().ToLowerInvariant();
            var title = (n.Title ?? string.Empty).Trim().ToLowerInvariant();

            // Messages are always clickable and open the provider chat.
            if (type.Contains("message") ||
                title.Contains("message") ||
                title.Contains("whatsapp"))
                return true;

            // Complaint and warning notifications are informational only.
            if (type.Contains("complaint") ||
                title.Contains("complaint") ||
                type.Contains("warning") ||
                title.Contains("warning"))
                return false;

            // Rating/review notifications are informational only.
            if (type.Contains("rating") ||
                title.Contains("rating") ||
                title.Contains("rated") ||
                title.Contains("review"))
                return false;

            // A rejected RESCHEDULE is different from a rejected booking.
            // The booking card still exists, so this notification may open it
            // while the booking is still active. Notifications() performs the
            // current-booking-state check as well.
            bool isRejectedReschedule =
                title.Contains("customer rejected reschedule") ||
                title.Contains("reschedule rejected") ||
                title.Contains("rejected reschedule");

            if (isRejectedReschedule)
                return true;

            // Full booking cancellation/rejection notifications open Job History
            // (the booking card is no longer on the dashboard).
            if (type.Contains("cancellation") ||
                type.Contains("cancel") ||
                type.Contains("reject") ||
                title.Contains("cancel") ||
                title.Contains("rejected") ||
                title.Contains("reject"))
                return true;

            // Actionable provider-side notifications. "Mark as Complete" is
            // clickable while the booking is still Accepted; a completed-booking
            // notification itself is informational and is therefore excluded.
            return title.Contains("new booking") ||
                   title.Contains("booking request") ||
                   title.Contains("booking accepted") ||
                   title.Contains("mark complete") ||
                   title.Contains("mark as complete") ||
                   title.Contains("marked complete") ||
                   title.Contains("marked as complete") ||
                   title.Contains("customer accepted reschedule") ||
                   title.Contains("reschedule accepted") ||
                   title.Contains("accepted reschedule") ||
                   title.Contains("reschedule request") ||
                   title.Contains("provider reschedule request") ||
                   (type.Contains("reschedule") &&
                    !type.Contains("reject") &&
                    !type.Contains("cancel"));
        }

        private static bool IsStatus(string? actualStatus, string expectedStatus)
        {
            return string.Equals(
                actualStatus?.Trim(),
                expectedStatus,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProviderDashboardBookingStatus(string? status)
        {
            var normalized = (status ?? string.Empty).Trim();

            return string.IsNullOrWhiteSpace(normalized) ||
                   normalized.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Accepted", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Reschedule Requested", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Rescheduled", StringComparison.OrdinalIgnoreCase);
        }

        // Provider notifications page

        // Notifications are shown 15 at a time. "Load more" calls
        // Notifications?take=30, then take=45, and so on.
        private const int NotificationPageSize = 15;

        [HttpGet]
        public async Task<IActionResult> Notifications(int take = NotificationPageSize)
        {
            if (take < NotificationPageSize)
                take = NotificationPageSize;

            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return RedirectToAction("Login", "Home");

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var providerIds = new List<string>();

            foreach (var value in new[] { providerId, providerEmail, providerCode })
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !providerIds.Contains(value))
                {
                    providerIds.Add(value);
                }
            }

            var notificationQuery = _context.Notifications
                .AsNoTracking()
                .Where(n =>
                    (providerIds.Contains(n.TargetUserId ?? string.Empty) ||
                     providerIds.Contains(n.UserId ?? string.Empty)) &&
                    !providerIds.Contains(n.SenderUserId ?? string.Empty));

            int totalNotificationCount = await notificationQuery.CountAsync();

            // Latest first. Only the first `take` (15, 30, 45...) are loaded.
            var notifications = await notificationQuery
                .OrderByDescending(n => n.Timestamp)
                .ThenByDescending(n => n.NotificationId)
                .Take(take)
                .ToListAsync();

            ViewBag.HasMoreNotifications = totalNotificationCount > notifications.Count;
            ViewBag.NextNotificationTake = take + NotificationPageSize;

            ViewBag.ProviderProfileImage = provider.ProfilePicPath;

            ViewBag.TotalCount = totalNotificationCount;
            ViewBag.UnreadCount = await GetProviderUnreadNotificationCountAsync(provider);

            var rejectedRescheduleBookingIds = notifications
                .Where(n =>
                    n.BookingId.HasValue &&
                    (
                        (n.Title ?? string.Empty).Contains(
                            "Customer Rejected Reschedule",
                            StringComparison.OrdinalIgnoreCase) ||
                        (n.Title ?? string.Empty).Contains(
                            "Reschedule Rejected",
                            StringComparison.OrdinalIgnoreCase) ||
                        (n.Title ?? string.Empty).Contains(
                            "Rejected Reschedule",
                            StringComparison.OrdinalIgnoreCase)
                    ))
                .Select(n => n.BookingId!.Value)
                .Distinct()
                .ToList();

            var activeRejectedRescheduleBookingIds = rejectedRescheduleBookingIds.Count == 0
                ? new HashSet<int>()
                : (await _context.Booking
                    .AsNoTracking()
                    .Where(b =>
                        rejectedRescheduleBookingIds.Contains(b.Id) &&
                        b.ProviderId == provider.Id &&
                        (
                            string.IsNullOrWhiteSpace(b.Status) ||
                            b.Status == "Pending" ||
                            b.Status == "Accepted" ||
                            b.Status == "Reschedule Requested" ||
                            b.Status == "Rescheduled"
                        ))
                    .Select(b => b.Id)
                    .ToListAsync())
                    .ToHashSet();

            ViewBag.ActiveRejectedRescheduleNotificationIds = notifications
                .Where(n =>
                    n.BookingId.HasValue &&
                    activeRejectedRescheduleBookingIds.Contains(n.BookingId.Value) &&
                    (
                        (n.Title ?? string.Empty).Contains(
                            "Customer Rejected Reschedule",
                            StringComparison.OrdinalIgnoreCase) ||
                        (n.Title ?? string.Empty).Contains(
                            "Reschedule Rejected",
                            StringComparison.OrdinalIgnoreCase) ||
                        (n.Title ?? string.Empty).Contains(
                            "Rejected Reschedule",
                            StringComparison.OrdinalIgnoreCase)
                    ))
                .Select(n => n.NotificationId)
                .ToHashSet();

            // Keep the UI clickable state in sync with the CURRENT booking
            // state. This prevents old booking/reschedule notifications from
            // remaining orange/clickable after the booking moves forward.
            var clickableNotificationIds =
                new HashSet<int>();

            var providerNotificationBookingIds = notifications
                .Where(n => n.BookingId.HasValue)
                .Select(n => n.BookingId!.Value)
                .Distinct()
                .ToList();

            var providerNotificationBookings =
                providerNotificationBookingIds.Count == 0
                    ? new Dictionary<int, Booking>()
                    : (await _context.Booking
                        .AsNoTracking()
                        .Where(b =>
                            providerNotificationBookingIds.Contains(b.Id) &&
                            b.ProviderId == provider.Id)
                        .ToListAsync())
                        .ToDictionary(b => b.Id);

            foreach (var notification in notifications)
            {
                if (!IsProviderNotificationClickable(notification))
                    continue;

                var title =
                    (notification.Title ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant();

                var type =
                    (notification.NotificationType ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant();

                bool isMessage =
                    type.Contains("message") ||
                    title.Contains("message") ||
                    title.Contains("whatsapp");

                // Messages are always clickable and go to Messages.
                if (isMessage)
                {
                    clickableNotificationIds.Add(notification.NotificationId);
                    continue;
                }

                if (!notification.BookingId.HasValue ||
                    !providerNotificationBookings.TryGetValue(
                        notification.BookingId.Value,
                        out var booking))
                {
                    continue;
                }

                bool isCompleted =
                    IsStatus(booking.Status, "Completed");

                bool isFinal =
                    isCompleted ||
                    IsStatus(booking.Status, "Cancelled") ||
                    IsStatus(booking.Status, "Rejected") ||
                    IsStatus(booking.Status, "Expired");

                bool isRating =
                    type.Contains("rating") ||
                    title.Contains("rating") ||
                    title.Contains("rated") ||
                    title.Contains("review");

                bool isComplete =
                    type.Contains("complete") ||
                    title.Contains("mark complete") ||
                    title.Contains("mark as complete") ||
                    title.Contains("marked complete") ||
                    title.Contains("marked as complete") ||
                    title.Contains("marked as completed") ||
                    title.Contains("booking completed") ||
                    title.Contains("service completed");

                bool isRescheduleRequest =
                    title.Contains("reschedule request") ||
                    title.Contains("provider reschedule request") ||
                    (type.Contains("reschedule") &&
                     !title.Contains("accepted") &&
                     !title.Contains("rejected"));

                bool isRejectedReschedule =
                    title.Contains("customer rejected reschedule") ||
                    title.Contains("reschedule rejected") ||
                    title.Contains("rejected reschedule");

                bool isAcceptedReschedule =
                    title.Contains("customer accepted reschedule") ||
                    title.Contains("reschedule accepted") ||
                    title.Contains("accepted reschedule");

                // The original reschedule request becomes non-clickable once
                // it has been accepted. The acceptance notification itself
                // remains clickable while the booking is active.
                if (isRescheduleRequest &&
                    IsStatus(booking.RescheduledStatus, "Accepted"))
                    continue;

                // Cancel / reject notifications stay clickable while the booking is
                // really cancelled, rejected or expired (they open Job History).
                // If the booking was accepted again, the old notification is view only.
                bool isCancelOrReject =
                    !isRejectedReschedule &&
                    (type.Contains("cancel") ||
                     type.Contains("reject") ||
                     title.Contains("cancel") ||
                     title.Contains("reject"));

                if (isCancelOrReject)
                {
                    if (isFinal && !isCompleted)
                        clickableNotificationIds.Add(notification.NotificationId);

                    continue;
                }

                // Other old notifications of a finished booking are view only.
                // Completed and Rating notifications are allowed only for a
                // genuinely completed booking.
                if (isFinal && !isCompleted && !isRating && !isComplete)
                    continue;

                // Once completed, the old New/Accepted/Reschedule activity
                // is no longer actionable. Only Complete and Rating remain.
                if (isCompleted && !isComplete && !isRating)
                    continue;

                // Rating/review notifications are informational only.
                if (isRating)
                    continue;

                // Mark-as-complete is actionable while the booking is Accepted.
                // Once the booking is already Completed, all old action notifications
                // become non-clickable through the isCompleted check above.
                if (isComplete && !IsStatus(booking.Status, "Accepted"))
                    continue;

                // A rejected reschedule can open the active booking only.
                if (isRejectedReschedule &&
                    !IsProviderDashboardBookingStatus(booking.Status))
                    continue;

                // Accepted reschedule and all other booking actions remain
                // clickable while the booking is still active.
                if (isAcceptedReschedule ||
                    isRejectedReschedule ||
                    isRescheduleRequest ||
                    title.Contains("new booking") ||
                    title.Contains("booking request") ||
                    title.Contains("booking accepted") ||
                    isComplete)
                {
                    clickableNotificationIds.Add(notification.NotificationId);
                }
            }

            ViewBag.ClickableNotificationIds =
                clickableNotificationIds;

            return View(notifications);
        }

        // Open provider notification
        [HttpGet]
        public async Task<IActionResult> OpenNotification(int id)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return RedirectToAction("Login", "Home");

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n =>
                    n.NotificationId == id &&
                    (n.TargetUserId == providerId ||
                     n.TargetUserId == providerEmail ||
                     n.TargetUserId == providerCode ||
                     n.UserId == providerId ||
                     n.UserId == providerEmail ||
                     n.UserId == providerCode) &&
                    !(n.SenderUserId == providerId ||
                      n.SenderUserId == providerEmail ||
                      n.SenderUserId == providerCode));

            if (notification == null)
                return RedirectToAction("Notifications", "Provider");

            // Server-side protection for view-only notifications.
            if (!IsProviderNotificationClickable(notification))
                return RedirectToAction("Notifications", "Provider");

            if (notification.BookingId.HasValue)
            {
                var booking = await _context.Booking
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b =>
                        b.Id == notification.BookingId.Value &&
                        b.ProviderId == provider.Id);

                bool isRejectedReschedule =
                    (notification.Title ?? string.Empty).Contains(
                        "Customer Rejected Reschedule",
                        StringComparison.OrdinalIgnoreCase) ||
                    (notification.Title ?? string.Empty).Contains(
                        "Reschedule Rejected",
                        StringComparison.OrdinalIgnoreCase) ||
                    (notification.Title ?? string.Empty).Contains(
                        "Rejected Reschedule",
                        StringComparison.OrdinalIgnoreCase);

                string notificationTitle =
                    (notification.Title ?? string.Empty).Trim().ToLowerInvariant();

                string notificationType =
                    (notification.NotificationType ?? string.Empty).Trim().ToLowerInvariant();

                bool isRescheduleRequestNotification =
                    notificationTitle.Contains("reschedule request") ||
                    notificationTitle.Contains("provider reschedule request") ||
                    (notificationType.Contains("reschedule") &&
                     !notificationTitle.Contains("accepted") &&
                     !notificationTitle.Contains("rejected"));

                bool isCancelOrRejectNotification =
                    !isRejectedReschedule &&
                    (notificationTitle.Contains("cancel") ||
                     notificationTitle.Contains("reject") ||
                     notificationType.Contains("cancel") ||
                     notificationType.Contains("reject"));

                if (isCancelOrRejectNotification)
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();

                    bool bookingIsFinal =
                        booking != null &&
                        (IsStatus(booking.Status, "Cancelled") ||
                         IsStatus(booking.Status, "Rejected") ||
                         IsStatus(booking.Status, "Expired"));

                    return bookingIsFinal
                        ? RedirectToAction("History", "Provider", new { bookingId = notification.BookingId })
                        : RedirectToAction("Notifications", "Provider");
                }

                bool bookingIsActiveForRejectedReschedule =
                    booking != null &&
                    IsProviderDashboardBookingStatus(booking.Status) &&
                    !string.Equals(
                        booking.Status?.Trim(),
                        "Completed",
                        StringComparison.OrdinalIgnoreCase);

                if (booking == null ||
                    (!isRejectedReschedule &&
                     !IsProviderDashboardBookingStatus(booking.Status)) ||
                    (isRejectedReschedule &&
                     !bookingIsActiveForRejectedReschedule))
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Provider");
                }

                // The original reschedule request is stale after acceptance.
                bool isMarkCompleteNotification =
                    notificationTitle.Contains("mark complete") ||
                    notificationTitle.Contains("mark as complete") ||
                    notificationTitle.Contains("marked complete") ||
                    notificationTitle.Contains("marked as complete");

                if (isMarkCompleteNotification &&
                    !string.Equals(
                        booking.Status?.Trim(),
                        "Accepted",
                        StringComparison.OrdinalIgnoreCase))
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Provider");
                }

                if (isRescheduleRequestNotification &&
                    string.Equals(
                        booking.RescheduledStatus?.Trim(),
                        "Accepted",
                        StringComparison.OrdinalIgnoreCase))
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Provider");
                }

                // After completion, every old actionable notification becomes
                // non-clickable. The completed card is no longer an action target.
                if (string.Equals(
                        booking.Status?.Trim(),
                        "Completed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Provider");
                }

            }

            notification.IsRead = true;
            await _context.SaveChangesAsync();

            var type = (notification.NotificationType ?? string.Empty).Trim().ToLowerInvariant();
            var title = (notification.Title ?? string.Empty).Trim().ToLowerInvariant();

            if (type.Contains("message") || title.Contains("message") || title.Contains("whatsapp"))
            {
                if (!string.IsNullOrWhiteSpace(notification.ActionUrl))
                    return LocalRedirect(notification.ActionUrl);

                return RedirectToAction("Messages", "Provider");
            }

            if (notification.BookingId.HasValue)
            {
                // Keep booking notifications on the provider dashboard.
                // bookingId is passed for dashboards that support focused navigation.
                return RedirectToAction("Dashboard", "Provider",
                    new { bookingId = notification.BookingId.Value });
            }

            if (!string.IsNullOrWhiteSpace(notification.ActionUrl))
                return LocalRedirect(notification.ActionUrl);

            return RedirectToAction("Notifications", "Provider");
        }

        // Mark notification as read

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkNotificationRead(int id)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return Unauthorized(new { success = false, message = "Session expired." });

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n =>
                    n.NotificationId == id &&
                    (
                        n.TargetUserId == providerId ||
                        n.TargetUserId == providerEmail ||
                        n.TargetUserId == providerCode ||
                        n.UserId == providerId ||
                        n.UserId == providerEmail ||
                        n.UserId == providerCode
                    ));

            if (notification == null)
                return NotFound(new { success = false, message = "Notification not found." });

            notification.IsRead = true;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // Dismiss one notification

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DismissNotification(int id)
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return Unauthorized(new { success = false, message = "Session expired." });

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n =>
                    n.NotificationId == id &&
                    (n.TargetUserId == providerId ||
                     n.TargetUserId == providerEmail ||
                     n.TargetUserId == providerCode ||
                     n.UserId == providerId ||
                     n.UserId == providerEmail ||
                     n.UserId == providerCode) &&
                    n.SenderUserId != providerId &&
                    n.SenderUserId != providerEmail &&
                    n.SenderUserId != providerCode);

            if (notification == null)
                return NotFound(new { success = false, message = "Notification not found." });

            _context.Notifications.Remove(notification);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // Clear all provider notifications

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearNotifications()
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return Unauthorized(new { success = false, message = "Session expired." });

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var notifications = await _context.Notifications
                .Where(n =>
                    (n.TargetUserId == providerId ||
                     n.TargetUserId == providerEmail ||
                     n.TargetUserId == providerCode ||
                     n.UserId == providerId ||
                     n.UserId == providerEmail ||
                     n.UserId == providerCode) &&
                    n.SenderUserId != providerId &&
                    n.SenderUserId != providerEmail &&
                    n.SenderUserId != providerCode)
                .ToListAsync();

            if (notifications.Count > 0)
            {
                _context.Notifications.RemoveRange(notifications);
                await _context.SaveChangesAsync();
            }

            return Json(new { success = true, count = notifications.Count });
        }

        // Mark all notifications as read

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllNotificationsRead()
        {
            var provider = await GetLoggedInProviderAsync();

            if (provider == null)
                return Unauthorized(new { success = false, message = "Session expired." });

            string providerId = provider.Id.ToString();
            string providerEmail = provider.Email ?? string.Empty;
            string providerCode = provider.ProviderId ?? string.Empty;

            var notifications = await _context.Notifications
                .Where(n =>
                    !n.IsRead &&
                    (
                        n.TargetUserId == providerId ||
                        n.TargetUserId == providerEmail ||
                        n.TargetUserId == providerCode ||
                        n.UserId == providerId ||
                        n.UserId == providerEmail ||
                        n.UserId == providerCode
                    ) &&
                    n.SenderUserId != providerId &&
                    n.SenderUserId != providerEmail &&
                    n.SenderUserId != providerCode)
                .ToListAsync();

            foreach (var notification in notifications)
                notification.IsRead = true;

            await _context.SaveChangesAsync();

            return Json(new { success = true, count = notifications.Count });
        }

        // Reschedule request model
        // Used by both the JSON request action and the legacy form action.
        // Keeping the model here removes the missing-type compilation error
        // without changing the existing request flow.
        public class RescheduleRequest
        {
            public int BookingId { get; set; }
            public int? Id { get; set; }
            public DateTime? NewDate { get; set; }
            public string NewTime { get; set; } = string.Empty;
        }

    }
}