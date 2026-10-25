using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceGo.Data;
using ServiceGo.Models;
using System.Globalization;

namespace ServiceGo.Controllers
{
    public class CustomerController : Controller
    {
        private readonly AppDbContext _context;

        public CustomerController(AppDbContext context)
        {
            _context = context;
        }

        // Provider notification helper

        private void AddProviderNotification(
            Register provider,
            string title,
            string message,
            string notificationType,
            string iconClass,
            int? bookingId = null,
            string? referenceId = null,
            string? actionUrl = null)
        {
            if (provider == null)
                return;

            string providerId = provider.Id.ToString();

            _context.Notifications.Add(new Notification
            {
                UserId = providerId,
                TargetUserId = providerId,
                SenderUserId = HttpContext.Session.GetString("UserCode") ?? "Customer",
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

        // Chat notifications for the customer
        // When a provider sends a text/voice message to this customer, the
        // customer gets a "New Message" notification. This is created here so it
        // works even if the provider side does not create one. A message that
        // already has a notification (same ReferenceId = MessageId) is skipped,
        // so there are no duplicates.
        // A per-session watermark stops notifications that the customer has
        // dismissed or cleared from being created again.
        private const string ChatNotifyWatermarkKey = "CustomerChatNotifyWatermark";

        private async Task SyncChatNotificationsAsync(CustomerSignup customer)
        {
            var ids = new List<string>();
            var messageIds = new List<string>();

            void AddId(List<string> list, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !list.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(value.Trim());
                }
            }

            // Ids used by Notifications.
            AddId(ids, customer.Email);
            AddId(ids, customer.CustomerId);
            AddId(ids, customer.Id.ToString());

            // Ids the customer can have on a Message row.
            AddId(messageIds, customer.Id.ToString());
            AddId(messageIds, "CUS-" + customer.Id);
            AddId(messageIds, "CUS" + customer.Id);
            AddId(messageIds, customer.CustomerId);
            AddId(messageIds, customer.Email);

            DateTime syncStart = DateTime.Now;
            DateTime watermark;

            if (long.TryParse(HttpContext.Session.GetString(ChatNotifyWatermarkKey), out long ticks))
            {
                watermark = new DateTime(ticks);
            }
            else
            {
                // First check in this login session: continue after the newest chat
                // notification that already exists, otherwise look at the last 24 hours.
                var latestChatNotification = await _context.Notifications
                    .AsNoTracking()
                    .Where(n =>
                        n.NotificationType == "Message" &&
                        (ids.Contains(n.UserId ?? string.Empty) ||
                         ids.Contains(n.TargetUserId ?? string.Empty)))
                    .MaxAsync(n => (DateTime?)n.Timestamp);

                watermark = latestChatNotification ?? syncStart.AddHours(-24);
            }

            var newMessages = await _context.Messages
                .AsNoTracking()
                .Where(m =>
                    !m.IsFromCustomer &&
                    messageIds.Contains(m.ReceiverId) &&
                    !m.DeletedForEveryone &&
                    !m.DeletedForReceiver &&
                    m.Timestamp > watermark)
                .OrderBy(m => m.Timestamp)
                .Take(50)
                .ToListAsync();

            // Move the watermark forward (small overlap, duplicates are skipped below).
            HttpContext.Session.SetString(
                ChatNotifyWatermarkKey,
                syncStart.AddSeconds(-2).Ticks.ToString());

            if (newMessages.Count == 0)
                return;

            var refs = newMessages.Select(m => m.MessageId.ToString()).ToList();

            var alreadyNotified = (await _context.Notifications
                .AsNoTracking()
                .Where(n =>
                    n.ReferenceId != null &&
                    refs.Contains(n.ReferenceId) &&
                    (ids.Contains(n.UserId ?? string.Empty) ||
                     ids.Contains(n.TargetUserId ?? string.Empty)))
                .Select(n => n.ReferenceId!)
                .ToListAsync())
                .ToHashSet();

            // Never use the bare numeric id here: customer id 1 and provider id 1
            // look the same and the provider would see this notification.
            string notificationUserId =
                !string.IsNullOrWhiteSpace(customer.CustomerId)
                    ? customer.CustomerId.Trim()
                    : (customer.Email ?? customer.Id.ToString());

            var providerCache = new Dictionary<string, Register?>(StringComparer.OrdinalIgnoreCase);
            bool added = false;

            foreach (var msg in newMessages)
            {
                if (alreadyNotified.Contains(msg.MessageId.ToString()))
                    continue;

                string senderKey = msg.SenderId ?? string.Empty;

                if (!providerCache.TryGetValue(senderKey, out var provider))
                {
                    provider = await ResolveProviderForMessagingAsync(senderKey);
                    providerCache[senderKey] = provider;
                }

                if (provider == null)
                    continue;

                bool isVoice = !string.IsNullOrWhiteSpace(msg.AudioUrl);
                string providerName = provider.Name ?? "Provider";

                _context.Notifications.Add(new Notification
                {
                    UserId = notificationUserId,
                    TargetUserId = notificationUserId,
                    SenderUserId = provider.Id.ToString(),
                    Title = isVoice ? "New Voice Message" : "New Message",
                    Message = isVoice
                        ? $"{providerName} sent you a voice message."
                        : $"{providerName} sent you a new message.",
                    NotificationType = "Message",
                    IconClass = isVoice ? "fa-solid fa-microphone" : "fa-solid fa-message",
                    IsRead = false,
                    Timestamp = msg.Timestamp,
                    ReferenceId = msg.MessageId.ToString(),
                    ActionUrl = Url.Action("Messages", "Customer", new { providerId = provider.Id })
                });

                added = true;
            }

            if (added)
                await _context.SaveChangesAsync();
        }

        private async Task<int> GetCustomerUnreadNotificationCountAsync(CustomerSignup customer)
        {
            // Make sure provider chat messages have their notification first,
            // so the badge count and the Notifications page include them.
            await SyncChatNotificationsAsync(customer);

            var ids = new List<string>();

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(value.Trim());
                }
            }

            Add(customer.Email);
            Add(customer.CustomerId);
            Add(customer.Id.ToString());

            var senderIds = GetCustomerSenderIds(customer);
            return await _context.Notifications
                .AsNoTracking()
                .CountAsync(n =>
                    !n.IsRead &&
                    (ids.Contains(n.UserId ?? string.Empty) ||
                     ids.Contains(n.TargetUserId ?? string.Empty)) &&
                    !senderIds.Contains(n.SenderUserId ?? string.Empty));
        }

        // Customer dashboard

        [HttpGet]
        public async Task<IActionResult> CusDashboard()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var userCode = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(userCode) &&
                !userId.HasValue)
            {
                return RedirectToAction("Login", "Home");
            }

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(userCode))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CustomerId == userCode);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            email = customer.Email ?? email;
            ViewBag.UnreadNotificationCount = await GetCustomerUnreadNotificationCountAsync(customer);

            string customerCode =
                !string.IsNullOrWhiteSpace(customer.CustomerId)
                    ? customer.CustomerId.Trim()
                    : $"CUS-{customer.Id}";

            // Expire old pending bookings
            // If a pending booking's scheduled date/time has passed
            // without being accepted or rejected, mark it Expired.
            int? focusBookingId = null;
            if (int.TryParse(Request.Query["bookingId"].ToString(), out var parsedFocusBookingId))
                focusBookingId = parsedFocusBookingId;

            // Tab requested by a notification click: pending / accepted / completed.
            var requestedTab = (Request.Query["tab"].ToString() ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            ViewBag.RequestedTab =
                (requestedTab == "pending" ||
                 requestedTab == "accepted" ||
                 requestedTab == "completed")
                    ? requestedTab
                    : string.Empty;

            var pendingBookings = await _context.Booking
                .Where(b =>
                    b.CustomerID == customerCode &&
                    (b.Status == "Pending" || b.Status == null))
                .ToListAsync();

            bool expiryChanged = false;

            foreach (var pendingBooking in pendingBookings)
            {
                if (ShouldExpirePendingBooking(
                    pendingBooking.BookingDate,
                    pendingBooking.BookingTime))
                {
                    pendingBooking.Status = "Expired";
                    pendingBooking.RescheduledStatus = "Expired";
                    pendingBooking.RequestedDate = null;
                    pendingBooking.RequestedTime = null;
                    pendingBooking.RescheduleRequestedBy = null;
                    pendingBooking.IsRescheduledByCustomer = false;
                    pendingBooking.IsRescheduledByProvider = false;
                    expiryChanged = true;
                }
            }

            if (expiryChanged)
            {
                await _context.SaveChangesAsync();
            }

            // Active bookings

            var bookings = await _context.Booking
                .AsNoTracking()
                .Where(b =>
                    b.CustomerID == customerCode &&
                    (
                        (b.Status != "Cancelled" &&
                         b.Status != "Rejected" &&
                         b.Status != "Expired")
                        ||
                        (focusBookingId.HasValue && b.Id == focusBookingId.Value)
                    ))
                .OrderByDescending(b => b.Id)
                .ToListAsync();

            var bookingList = new List<BookingDetailViewModel>();

            // Providers for view
            // Load all providers in one query instead of querying
            // Register once for every booking. This keeps the existing
            // booking/provider behaviour but removes the N+1 query cost.

            var providerIdsForDashboard = bookings
                .Where(b => b.ProviderId > 0)
                .Select(b => b.ProviderId)
                .Distinct()
                .ToList();

            var providersForDashboard = providerIdsForDashboard.Count > 0
                ? await _context.Register
                    .AsNoTracking()
                    .Where(r => providerIdsForDashboard.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id)
                : new Dictionary<int, Register>();

            // IDs for view

            var rescheduleAllowedIds = new HashSet<int>();
            var ratingPendingIds = new HashSet<int>();
            var cancellationAllowedIds = new HashSet<int>();
            var customerComplaintBookingIds = new HashSet<int>();

            var complaintBookingProperty = typeof(ComplaintModel).GetProperty("BookingId");
            if (complaintBookingProperty != null && !string.IsNullOrWhiteSpace(email))
            {
                var customerComplaints = await _context.Complaints
                    .AsNoTracking()
                    .Where(c => c.CustomerEmail == email)
                    .ToListAsync();

                foreach (var complaint in customerComplaints)
                {
                    var value = Convert.ToString(complaintBookingProperty.GetValue(complaint));
                    if (int.TryParse(value, out var complaintBookingId) && complaintBookingId > 0)
                    {
                        customerComplaintBookingIds.Add(complaintBookingId);
                    }
                }
            }

            // Safety net: a complaint notification is always saved with the booking id.
            // If the complaint row itself does not expose BookingId, this still makes
            // sure the File Complaint button is hidden once a complaint was filed.
            var complaintNotifiedBookingIds = await _context.Notifications
                .AsNoTracking()
                .Where(n =>
                    n.NotificationType == "Complaint" &&
                    n.UserId == "Admin" &&
                    n.BookingId.HasValue &&
                    n.SenderUserId == customer.Id.ToString())
                .Select(n => n.BookingId!.Value)
                .ToListAsync();

            if (complaintNotifiedBookingIds.Count > 0)
            {
                var ownBookingIds = bookings.Select(x => x.Id).ToHashSet();
                foreach (var cid in complaintNotifiedBookingIds)
                {
                    if (ownBookingIds.Contains(cid))
                        customerComplaintBookingIds.Add(cid);
                }
            }

            foreach (var b in bookings)
            {
                Register? provider = null;

                if (b.ProviderId > 0)
                {
                    providersForDashboard.TryGetValue(
                        b.ProviderId,
                        out provider);
                }

                // Address + map location

                string? selectedLat = null;
                string? selectedLng = null;

                string finalAddress = string.Empty;

                string serviceType =
                    b.ServiceType?.Trim() ?? string.Empty;

                bool isShopService =
                    serviceType.Equals("Shop", StringComparison.OrdinalIgnoreCase) ||
                    serviceType.Equals("Shop Service", StringComparison.OrdinalIgnoreCase);

                bool isHomeService =
                    serviceType.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                    serviceType.Equals("Home Service", StringComparison.OrdinalIgnoreCase);

                if (provider != null)
                {
                    if (isShopService)
                    {
                        // Customer selected Shop: use the provider's Shop location.
                        finalAddress =
                            !string.IsNullOrWhiteSpace(
                                provider.ShopAddressInput)
                                ? provider.ShopAddressInput
                                : string.Empty;

                        selectedLat =
                            provider.ShopLat?
                                .ToString(
                                    CultureInfo.InvariantCulture);

                        selectedLng =
                            provider.ShopLng?
                                .ToString(
                                    CultureInfo.InvariantCulture);

                        // If this provider only has a Home location saved, use that
                        // provider location rather than ever falling back to the
                        // customer's Booking.Address / Booking.Coordinates.
                        if (string.IsNullOrWhiteSpace(finalAddress) &&
                            !string.IsNullOrWhiteSpace(provider.HomeAddressInput))
                        {
                            finalAddress = provider.HomeAddressInput;
                        }

                        if (string.IsNullOrWhiteSpace(selectedLat) &&
                            provider.HomeLat.HasValue &&
                            provider.HomeLng.HasValue)
                        {
                            selectedLat = provider.HomeLat.Value
                                .ToString(CultureInfo.InvariantCulture);
                            selectedLng = provider.HomeLng.Value
                                .ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    else if (isHomeService)
                    {
                        // Customer selected Home: use the provider's Home location.
                        finalAddress =
                            !string.IsNullOrWhiteSpace(
                                provider.HomeAddressInput)
                                ? provider.HomeAddressInput
                                : string.Empty;

                        selectedLat =
                            provider.HomeLat?
                                .ToString(
                                    CultureInfo.InvariantCulture);

                        selectedLng =
                            provider.HomeLng?
                                .ToString(
                                    CultureInfo.InvariantCulture);

                        // If this provider only has a Shop location saved, use that
                        // provider location rather than the customer's booking location.
                        if (string.IsNullOrWhiteSpace(finalAddress) &&
                            !string.IsNullOrWhiteSpace(provider.ShopAddressInput))
                        {
                            finalAddress = provider.ShopAddressInput;
                        }

                        if (string.IsNullOrWhiteSpace(selectedLat) &&
                            provider.ShopLat.HasValue &&
                            provider.ShopLng.HasValue)
                        {
                            selectedLat = provider.ShopLat.Value
                                .ToString(CultureInfo.InvariantCulture);
                            selectedLng = provider.ShopLng.Value
                                .ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }

                // Provider address fallback
                // Never use Booking.Address / Booking.Coordinates here,
                // because those belong to the customer's booking location.

                if (provider != null && string.IsNullOrWhiteSpace(finalAddress))
                {
                    finalAddress =
                        !string.IsNullOrWhiteSpace(provider.LocalAddress)
                            ? provider.LocalAddress
                            : provider.PermanentAddress ?? string.Empty;
                }

                // Provider name

                string resolvedProviderName =
                    !string.IsNullOrWhiteSpace(b.ProviderName)
                        ? b.ProviderName
                        : provider?.Name ?? "Not Assigned";

                // Reschedule request state

                bool hasPendingReschedule =
                    HasPendingReschedule(b);

                bool hasAnyRescheduleRequest =
                    HasAnyRescheduleRequest(b);

                bool hasCustomerRescheduleRequest =
                    b.IsRescheduledByCustomer ||
                    IsStatus(
                        b.RescheduleRequestedBy,
                        "Customer");

                // Reschedule button
                // Customer can reschedule only a Pending booking.
                // If the customer has already sent a request once,
                // the button must not appear again.
                // Provider requests are handled separately through
                // View Reschedule Request in the view.

                bool isRescheduleAllowed =
                    (IsStatus(b.Status, "Pending") ||
                     (IsStatus(b.Status, "Accepted") &&
                      IsCancellationAllowed(b))) &&
                    !hasAnyRescheduleRequest &&
                    !hasPendingReschedule &&
                    !hasCustomerRescheduleRequest;

                if (isRescheduleAllowed)
                {
                    rescheduleAllowedIds.Add(b.Id);
                }

                // Cancellation: Pending is always allowed; Accepted is allowed
                // only before the two-hour cutoff.
                if (IsCancellationAllowed(b))
                {
                    cancellationAllowedIds.Add(b.Id);
                }

                // Rating

                bool ratingPending =
                    IsStatus(b.Status, "Completed") &&
                    !b.Rating.HasValue;

                if (ratingPending)
                {
                    ratingPendingIds.Add(b.Id);
                }

                // Map link

                string mapLink = "#";

                if (!string.IsNullOrWhiteSpace(selectedLat) &&
                    !string.IsNullOrWhiteSpace(selectedLng))
                {
                    mapLink =
                        $"https://www.google.com/maps?q={selectedLat},{selectedLng}";
                }
                else if (!string.IsNullOrWhiteSpace(finalAddress))
                {
                    mapLink =
                        $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(finalAddress)}";
                }

                // View model

                var detail = new BookingDetailViewModel
                {
                    Id = b.Id.ToString(),

                    BookingId = b.BookingId ?? string.Empty,

                    BookingDate = b.BookingDate,

                    BookingTime = b.BookingTime ?? string.Empty,

                    ServiceName = b.ServiceName ?? string.Empty,

                    ServiceType = b.ServiceType ?? string.Empty,

                    Status =
                        string.IsNullOrWhiteSpace(b.Status)
                            ? "Pending"
                            : b.Status,

                    HasRescheduleRequest =
                        hasAnyRescheduleRequest,

                    RescheduleRequestedBy =
                        b.RescheduleRequestedBy,

                    IsRescheduledByProvider =
                        b.IsRescheduledByProvider,

                    IsRescheduledByCustomer =
                        b.IsRescheduledByCustomer,

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

                    CustomerName =
                        customer.Name ?? b.CustomerName ?? "Customer",

                    CustomerId =
                        b.CustomerID ?? string.Empty,

                    Address =
                        b.Address ?? string.Empty,

                    ProviderAddress =
                        finalAddress,

                    MapLink =
                        mapLink,

                    ShopLat =
                        isShopService
                            ? selectedLat ?? string.Empty
                            : string.Empty,

                    ShopLng =
                        isShopService
                            ? selectedLng ?? string.Empty
                            : string.Empty,

                    ShopAddressInput =
                        isShopService
                            ? finalAddress
                            : string.Empty,

                    HomeLat =
                        isHomeService
                            ? selectedLat ?? string.Empty
                            : string.Empty,

                    HomeLng =
                        isHomeService
                            ? selectedLng ?? string.Empty
                            : string.Empty,

                    HomeAddressInput =
                        isHomeService
                            ? finalAddress
                            : string.Empty,

                    ProviderId =
                        b.ProviderId.ToString(),

                    ProviderName =
                        resolvedProviderName,

                    ProviderLocation =
                        provider?.City ?? "N/A",

                    ProviderServiceType =
                        provider?.SelectedServices ?? b.ServiceName ?? string.Empty,

                    Rating =
                        provider?.Rating ?? 0,

                    AvailableDays =
                        provider?.AvailableDays ?? "N/A",

                    AvailableTime =
                        provider != null
                            ? $"{provider.StartTime} - {provider.EndTime}"
                            : "N/A",

                    VisitCharges =
                        b.VisitCharges ?? 0m
                };

                bookingList.Add(detail);
            }

            // Dashboard booking activity order
            // Pending uses the booking Id as the creation-order fallback.
            // Accepted uses the actual customer-facing "Booking Accepted"
            // notification timestamp, because Booking does not store an
            // AcceptedAt field. Completed uses Booking.CompletedAt.
            // Notifications store the database Booking.Id (int), while
            // BookingDetailViewModel.Id is exposed to the view as a string.
            // Keep those two key types separate so the ordering stays strongly typed.
            var dashboardBookingIds = bookings
                .Select(b => b.Id)
                .Distinct()
                .ToList();

            var acceptedNotificationTimes = dashboardBookingIds.Count > 0
                ? await _context.Notifications
                    .AsNoTracking()
                    .Where(n =>
                        n.BookingId.HasValue &&
                        dashboardBookingIds.Contains(n.BookingId.Value) &&
                        (
                            (n.Title ?? string.Empty).ToLower().Contains("booking accepted") ||
                            (n.NotificationType ?? string.Empty).ToLower().Contains("accept")
                        ))
                    .GroupBy(n => n.BookingId!.Value)
                    .Select(g => new
                    {
                        BookingId = g.Key,
                        Timestamp = g.Max(x => x.Timestamp)
                    })
                    .ToDictionaryAsync(x => x.BookingId, x => x.Timestamp)
                : new Dictionary<int, DateTime>();

            var bookingEntitiesById = bookings
                .ToDictionary(b => b.Id.ToString(), b => b);

            var bookingActivityTimes = new Dictionary<string, DateTime>();

            foreach (var booking in bookingList)
            {
                if (string.Equals(booking.Status, "Accepted", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(booking.Id, out var bookingDbId) &&
                    acceptedNotificationTimes.TryGetValue(bookingDbId, out var acceptedAt))
                {
                    bookingActivityTimes[booking.Id] = acceptedAt;
                }
                else if (string.Equals(booking.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                         bookingEntitiesById.TryGetValue(booking.Id, out var bookingEntity))
                {
                    bookingActivityTimes[booking.Id] = bookingEntity.CompletedAt ?? DateTime.MinValue;
                }
                else
                {
                    bookingActivityTimes[booking.Id] = DateTime.MinValue;
                }
            }

            ViewBag.BookingActivityTimes = bookingActivityTimes;

            // Viewbags

            ViewBag.RescheduleAllowedIds =
                rescheduleAllowedIds;

            ViewBag.RatingPendingIds =
                ratingPendingIds;

            ViewBag.CancellationAllowedIds =
                cancellationAllowedIds;

            ViewBag.CustomerComplaintBookingIds =
                customerComplaintBookingIds;

            ViewBag.RescheduleCutoffHours = 2;

            // Permanent customer profile values for the dashboard/profile popup.
            // These values come from CustomerSignup, never from Booking.
            ViewBag.CustomerPhonePermanent = customer.Phone ?? string.Empty;
            ViewBag.CustomerCity = customer.City ?? string.Empty;
            ViewBag.CustomerAddress = customer.Address ?? string.Empty;
            ViewBag.CustomerProfileImage = customer.ProfileImage;
            ViewBag.CustomerLatitude = customer.Latitude;
            ViewBag.CustomerLongitude = customer.Longitude;

            var viewModel = new CustomerDashboardViewModel
            {
                CustomerName =
                    customer.Name ?? "User",

                CustomerEmail =
                    email,

                // Permanent customer profile data comes from CustomerSignup.
                // Booking data is NOT used as the customer's profile source.
                CustomerPhone =
                    customer.Phone ?? "N/A",

                CustomerId =
                    customer.CustomerId ?? $"CUS-{customer.Id}",

                Bookings =
                    bookingList
            };

            return View(viewModel);
        }

        // Helper
        // Booking status check

        private static bool IsStatus(
            string? actualStatus,
            string expectedStatus)
        {
            return string.Equals(
                actualStatus?.Trim(),
                expectedStatus,
                StringComparison.OrdinalIgnoreCase);
        }

        // Helper
        // Final booking status

        private static bool IsFinalBookingStatus(
            string? status)
        {
            return
                IsStatus(status, "Cancelled") ||
                IsStatus(status, "Rejected") ||
                IsStatus(status, "Expired");
        }

        // Pending bookings expire as soon as their scheduled date/time has passed.
        private static bool ShouldExpirePendingBooking(
            DateTime bookingDate,
            string? bookingTime)
        {
            if (string.IsNullOrWhiteSpace(bookingTime))
                return false;

            if (!TimeSpan.TryParse(
                bookingTime.Trim(),
                out var time))
            {
                return false;
            }

            var bookingDateTime =
                bookingDate.Date.Add(time);

            return DateTime.Now >= bookingDateTime;
        }

        // Helper
        // Any reschedule request

        private static bool HasAnyRescheduleRequest(
            Booking booking)
        {
            // A request exists if:
            // 1. Customer sent it
            // 2. Provider sent it
            // 3. There is a pending request status
            // 4. There is requested date/time data
            // 5. There is an accepted/rejected reschedule record

            return
                booking.IsRescheduledByCustomer ||
                booking.IsRescheduledByProvider ||
                !string.IsNullOrWhiteSpace(
                    booking.RescheduleRequestedBy) ||
                booking.RequestedDate.HasValue ||
                !string.IsNullOrWhiteSpace(
                    booking.RequestedTime) ||
                !string.IsNullOrWhiteSpace(
                    booking.RescheduledStatus) ||
                booking.RescheduledDate.HasValue ||
                !string.IsNullOrWhiteSpace(
                    booking.RescheduledTime);
        }

        // Helper
        // Pending reschedule request

        private static bool HasPendingReschedule(
            Booking booking)
        {
            return
                IsStatus(
                    booking.RescheduledStatus,
                    "Pending")
                &&
                (
                    booking.IsRescheduledByCustomer ||
                    booking.IsRescheduledByProvider ||
                    !string.IsNullOrWhiteSpace(
                        booking.RescheduleRequestedBy)
                );
        }

        // Check accepted booking reschedule window

        private bool IsRescheduleAllowedForAcceptedBooking(
            DateTime bookingDate,
            string? bookingTime)
        {
            if (string.IsNullOrWhiteSpace(bookingTime))
                return false;

            DateTime parsedTime;

            string[] formats =
            {
                "h:mm tt",
                "hh:mm tt",
                "H:mm",
                "HH:mm",
                "h:mm",
                "hh:mm",
                "h:mm:ss tt",
                "hh:mm:ss tt",
                "H:mm:ss",
                "HH:mm:ss"
            };

            bool parsed =
                DateTime.TryParseExact(
                    bookingTime.Trim(),
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out parsedTime);

            if (!parsed)
            {
                parsed =
                    DateTime.TryParse(
                        bookingTime.Trim(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out parsedTime);
            }

            if (!parsed)
                return false;

            var bookingDateTime =
                bookingDate.Date.Add(parsedTime.TimeOfDay);

            var rescheduleCutoff =
                bookingDateTime.AddHours(-2);

            return DateTime.Now <= rescheduleCutoff;
        }

        // Check cancel booking window
        // Pending: cancel anytime while status is Pending.
        // Accepted: cancel only before two hours remain.
        // At exactly two hours before the booking, cancellation closes.
        private bool IsCancellationAllowed(Booking booking)
        {
            if (booking == null)
                return false;

            if (IsStatus(booking.Status, "Pending"))
                return true;

            if (!IsStatus(booking.Status, "Accepted"))
                return false;

            if (!TryGetBookingStartDateTime(
                    booking.BookingDate,
                    booking.BookingTime,
                    out var bookingDateTime))
            {
                return false;
            }

            var cancellationCutoff = bookingDateTime.AddHours(-2);
            return DateTime.Now < cancellationCutoff;
        }

        private static bool TryGetBookingStartDateTime(
            DateTime bookingDate,
            string? bookingTime,
            out DateTime bookingDateTime)
        {
            bookingDateTime = default;

            if (string.IsNullOrWhiteSpace(bookingTime))
                return false;

            string value = bookingTime.Trim();
            int dashIndex = value.IndexOf('-');

            if (dashIndex > 0)
                value = value[..dashIndex].Trim();

            string[] formats =
            {
                "h:mm tt", "hh:mm tt",
                "H:mm", "HH:mm",
                "h:mm", "hh:mm",
                "h:mm:ss tt", "hh:mm:ss tt",
                "H:mm:ss", "HH:mm:ss"
            };

            if (!DateTime.TryParseExact(
                    value,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsedTime))
            {
                if (!DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out parsedTime))
                    return false;
            }

            bookingDateTime = bookingDate.Date.Add(parsedTime.TimeOfDay);
            return true;
        }

        // Customer submits reschedule request

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitReschedule(
            int id,
            DateTime newDate,
            string? newTime)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            // Login check

            if (string.IsNullOrWhiteSpace(email))
            {
                return Unauthorized(new
                {
                    success = false,
                    status = "error",
                    message =
                        "Your session has expired. Please login again."
                });
            }

            // Date validation

            if (newDate == default)
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message = "New date is required."
                });
            }

            if (newDate.Date < DateTime.Today)
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message = "New date cannot be in the past."
                });
            }

            // Time validation

            if (string.IsNullOrWhiteSpace(newTime))
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message = "New time is required."
                });
            }

            newTime = newTime.Trim();

            // GET booking

            var booking =
                await _context.Booking
                    .FirstOrDefaultAsync(b =>
                        b.Id == id &&
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    status = "error",
                    message = "Booking not found."
                });
            }

            // Final booking status

            if (IsFinalBookingStatus(booking.Status) ||
                IsStatus(booking.Status, "Completed"))
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message =
                        "This booking can no longer be rescheduled."
                });
            }

            // One reschedule per booking
            // A pending/accepted/rejected reschedule attempt consumes the
            // single reschedule opportunity for this booking.
            if (booking.RescheduledDate.HasValue ||
                IsStatus(booking.RescheduledStatus, "Rejected") ||
                booking.IsRescheduledByCustomer ||
                booking.IsRescheduledByProvider)
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message = "This booking has already used its one reschedule request."
                });
            }

            // Pending bookings only
            // Accepted bookings must never be rescheduled.
            if (!IsStatus(booking.Status, "Pending"))
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message =
                        "Only pending bookings can be rescheduled."
                });
            }

            // Customer can request reschedule only once

            if (booking.IsRescheduledByCustomer ||
                IsStatus(
                    booking.RescheduleRequestedBy,
                    "Customer"))
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message =
                        "You have already submitted a reschedule request for this booking."
                });
            }

            // Existing pending request

            if (HasPendingReschedule(booking))
            {
                string requester;

                if (booking.IsRescheduledByProvider ||
                    IsStatus(
                        booking.RescheduleRequestedBy,
                        "Provider"))
                {
                    requester = "Provider";
                }
                else
                {
                    requester = "You";
                }

                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message =
                        $"{requester} already has a pending reschedule request. Please respond to it first."
                });
            }

            // Same date + time check

            bool sameDate =
                booking.BookingDate.Date ==
                newDate.Date;

            bool sameTime =
                string.Equals(
                    booking.BookingTime?.Trim(),
                    newTime,
                    StringComparison.OrdinalIgnoreCase);

            if (sameDate && sameTime)
            {
                return BadRequest(new
                {
                    success = false,
                    status = "error",
                    message =
                        "Please select a different date or time."
                });
            }

            // Clear old temporary request

            booking.RequestedDate = null;
            booking.RequestedTime = null;

            booking.IsRescheduledByCustomer = false;
            booking.IsRescheduledByProvider = false;

            booking.RescheduleRequestedBy = null;

            // Customer reschedule request

            booking.RequestedDate =
                newDate.Date;

            booking.RequestedTime =
                newTime;

            // These properties match the Booking model.

            booking.IsRescheduledByCustomer =
                true;

            booking.IsRescheduledByProvider =
                false;

            booking.RescheduleRequestedBy =
                "Customer";

            booking.RescheduledStatus =
                "Pending";

            // Provider notification

            if (booking.ProviderId > 0)
            {
                var provider = await _context.Register
                    .FirstOrDefaultAsync(p => p.Id == booking.ProviderId);

                if (provider != null)
                {
                    AddProviderNotification(
                        provider,
                        "Reschedule Request",
                        $"{booking.CustomerName ?? "Customer"} requested to reschedule booking {booking.BookingId ?? booking.Id.ToString()} to {booking.RequestedDate:dd MMM yyyy} at {booking.RequestedTime}.",
                        "Reschedule",
                        "fa-solid fa-calendar-days",
                        booking.Id,
                        booking.BookingId,
                        Url.Action("Dashboard", "Provider"));
                }
            }

            // Save

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return StatusCode(409, new
                {
                    success = false,
                    status = "error",
                    message =
                        "This booking was updated by another request. Please refresh the page and try again."
                });
            }
            catch (DbUpdateException ex)
            {
                var errorMessage =
                    ex.InnerException?.Message
                    ?? ex.Message;

                return StatusCode(500, new
                {
                    success = false,
                    status = "error",
                    message =
                        "Reschedule request database mein save nahi ho saki: "
                        + errorMessage
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    status = "error",
                    message =
                        "Reschedule request submit karte waqt error aa gaya: "
                        + ex.Message
                });
            }

            // Success response

            return Ok(new
            {
                success = true,

                status = "success",

                message =
                    "Your reschedule request is submitted successfully.",

                id =
                    booking.Id,

                bookingId =
                    booking.BookingId,

                bookingStatus =
                    booking.Status,

                rescheduleStatus =
                    booking.RescheduledStatus,

                requestedBy =
                    booking.RescheduleRequestedBy,

                requestedDate =
                    booking.RequestedDate?
                        .ToString(
                            "yyyy-MM-dd"),

                requestedTime =
                    booking.RequestedTime,

                isRescheduledByCustomer =
                    booking.IsRescheduledByCustomer,

                isRescheduledByProvider =
                    booking.IsRescheduledByProvider
            });
        }

        // View reschedule request

        [HttpGet]
        public async Task<IActionResult> ViewRescheduleRequest(
            int id)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return Unauthorized(new
                {
                    success = false,
                    message =
                        "Your session has expired."
                });
            }

            var booking =
                await _context.Booking
                    .FirstOrDefaultAsync(b =>
                        b.Id == id &&
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message =
                        "Booking not found."
                });
            }

            bool hasRequest =
                HasAnyRescheduleRequest(booking);

            if (!hasRequest)
            {
                return NotFound(new
                {
                    success = false,
                    message =
                        "No reschedule request found."
                });
            }

            string requestedBy;

            if (booking.IsRescheduledByCustomer ||
                IsStatus(
                    booking.RescheduleRequestedBy,
                    "Customer"))
            {
                requestedBy = "Customer";
            }
            else if (
                booking.IsRescheduledByProvider ||
                IsStatus(
                    booking.RescheduleRequestedBy,
                    "Provider"))
            {
                requestedBy = "Provider";
            }
            else
            {
                requestedBy =
                    booking.RescheduleRequestedBy
                    ?? "Completed Request";
            }

            return Json(new
            {
                success = true,

                bookingId =
                    booking.BookingId,

                databaseId =
                    booking.Id,

                requestedBy =
                    requestedBy,

                status =
                    booking.RescheduledStatus
                    ?? "Pending",

                bookingStatus =
                    booking.Status,

                originalDate =
                    booking.BookingDate
                        .ToString(
                            "dd MMM yyyy"),

                originalTime =
                    booking.BookingTime,

                requestedDate =
                    booking.RequestedDate.HasValue
                        ? booking.RequestedDate.Value
                            .ToString(
                                "dd MMM yyyy")
                        : null,

                requestedTime =
                    booking.RequestedTime,

                acceptedDate =
                    booking.RescheduledDate.HasValue
                        ? booking.RescheduledDate.Value
                            .ToString(
                                "dd MMM yyyy")
                        : null,

                acceptedTime =
                    booking.RescheduledTime,

                isCustomerRequest =
                    booking.IsRescheduledByCustomer,

                isProviderRequest =
                    booking.IsRescheduledByProvider,

                canCustomerRespond =
                    booking.IsRescheduledByProvider &&
                    IsStatus(
                        booking.RescheduledStatus,
                        "Pending")
            });
        }

        // Fixed booking commission
        // A fixed Rs.10 is charged for an accepted booking.
        // VisitCharges is NOT used to calculate commission.
        private const decimal BookingCommission = 10m;

        // Customer responds to provider reschedule

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RespondReschedule(
            int id,
            string? action)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return Unauthorized(new
                {
                    success = false,
                    message =
                        "Your session has expired."
                });
            }

            if (string.IsNullOrWhiteSpace(action))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Invalid action."
                });
            }

            action = action.Trim();

            var booking =
                await _context.Booking
                    .FirstOrDefaultAsync(b =>
                        b.Id == id &&
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

            if (booking == null)
            {
                return NotFound(new
                {
                    success = false,
                    message =
                        "Booking not found."
                });
            }

            // Provider request check

            bool providerRequest =
                booking.IsRescheduledByProvider ||
                IsStatus(
                    booking.RescheduleRequestedBy,
                    "Provider");

            if (!providerRequest ||
                !IsStatus(
                    booking.RescheduledStatus,
                    "Pending"))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "No pending provider reschedule request found."
                });
            }

            // Accept

            if (action.Equals(
                "Accept",
                StringComparison.OrdinalIgnoreCase))
            {
                var provider =
                    await _context.Register
                        .FirstOrDefaultAsync(p =>
                            p.Id == booking.ProviderId &&
                            p.Role == "Provider");

                if (provider == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Provider account not found for this booking."
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
                            "Reschedule date or time is missing."
                    });
                }

                // Final booking date
                booking.BookingDate =
                    booking.RequestedDate.Value.Date;

                booking.BookingTime =
                    booking.RequestedTime.Trim();

                // Final accepted reschedule
                booking.RescheduledDate =
                    booking.RequestedDate.Value.Date;

                booking.RescheduledTime =
                    booking.RequestedTime.Trim();

                booking.RescheduledStatus =
                    "Accepted";

                // Request complete
                booking.RequestedDate =
                    null;

                booking.RequestedTime =
                    null;

                booking.IsRescheduledByProvider =
                    false;

                booking.IsRescheduledByCustomer =
                    false;

                booking.RescheduleRequestedBy =
                    null;

                // Fixed commission: Rs.10 per accepted booking.
                // VisitCharges is not used for commission calculation.
                decimal commission = BookingCommission;

                if (provider.WalletBalance < commission)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"Insufficient provider wallet balance. Rs. {commission:0.##} is required to accept this booking.",
                        walletBalance = provider.WalletBalance,
                        commission = commission
                    });
                }

                if (!booking.WalletCommissionDeducted)
                {
                    provider.WalletBalance -= commission;
                    booking.WalletCommissionDeducted = true;
                    _context.Register.Update(provider);
                }

                booking.Status =
                    "Accepted";
            }

            // Reject

            else if (action.Equals(
                "Reject",
                StringComparison.OrdinalIgnoreCase))
            {
                booking.RescheduledStatus =
                    "Rejected";

                booking.RequestedDate =
                    null;

                booking.RequestedTime =
                    null;

                booking.IsRescheduledByProvider =
                    false;

                booking.IsRescheduledByCustomer =
                    false;

                booking.RescheduleRequestedBy =
                    null;
            }
            else
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Invalid action."
                });
            }

            // Provider notification

            if (booking.ProviderId > 0)
            {
                var provider = await _context.Register
                    .FirstOrDefaultAsync(p => p.Id == booking.ProviderId);

                if (provider != null)
                {
                    bool accepted = action.Equals(
                        "Accept",
                        StringComparison.OrdinalIgnoreCase);

                    AddProviderNotification(
                        provider,
                        accepted ? "Customer Accepted Reschedule" : "Customer Rejected Reschedule",
                        accepted
                            ? $"{booking.CustomerName ?? "Customer"} accepted your reschedule request for booking {booking.BookingId ?? booking.Id.ToString()}."
                            : $"{booking.CustomerName ?? "Customer"} rejected your reschedule request for booking {booking.BookingId ?? booking.Id.ToString()}.",
                        "Booking",
                        accepted ? "fa-solid fa-calendar-check" : "fa-solid fa-calendar-xmark",
                        booking.Id,
                        booking.BookingId,
                        Url.Action("Dashboard", "Provider"));
                }
            }

            // Save

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return StatusCode(409, new
                {
                    success = false,
                    message =
                        "This reschedule request was already updated. Please refresh the page."
                });
            }
            catch (DbUpdateException ex)
            {
                var errorMessage =
                    ex.InnerException?.Message
                    ?? ex.Message;

                return StatusCode(500, new
                {
                    success = false,
                    message =
                        "Reschedule response database mein save nahi ho saka: "
                        + errorMessage
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message =
                        "Reschedule response submit karte waqt error aa gaya: "
                        + ex.Message
                });
            }

            // Success

            return Ok(new
            {
                success = true,

                message =
                    action.Equals(
                        "Accept",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Provider reschedule accepted successfully."
                        : "Provider reschedule rejected successfully.",

                status =
                    booking.RescheduledStatus,

                bookingStatus =
                    booking.Status,

                commission =
                    action.Equals(
                        "Accept",
                        StringComparison.OrdinalIgnoreCase)
                        ? BookingCommission
                        : 0m,

                walletBalance =
                    action.Equals(
                        "Accept",
                        StringComparison.OrdinalIgnoreCase)
                        ? (await _context.Register
                            .Where(p =>
                                p.Id == booking.ProviderId &&
                                p.Role == "Provider")
                            .Select(p => p.WalletBalance)
                            .FirstOrDefaultAsync())
                        : 0m,

                bookingId =
                    booking.BookingId,

                bookingDate =
                    booking.BookingDate
                        .ToString(
                            "yyyy-MM-dd"),

                bookingTime =
                    booking.BookingTime
            });
        }

        // Rate provider

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RateProvider(
            int bookingId,
            int providerId,
            int rating,
            string? comment)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToAction(
                    "Login",
                    "Home");
            }

            if (rating < 1 || rating > 5)
            {
                TempData["ErrorMessage"] =
                    "Please select a rating between 1 and 5.";

                return RedirectToAction("History");
            }

            var booking =
                await _context.Booking
                    .FirstOrDefaultAsync(b =>
                        b.Id == bookingId &&
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

            if (booking == null)
            {
                TempData["ErrorMessage"] =
                    "Booking not found.";

                return RedirectToAction("History");
            }

            if (!IsStatus(
                booking.Status,
                "Completed"))
            {
                TempData["ErrorMessage"] =
                    "You can only rate a completed service.";

                return RedirectToAction("History");
            }

            if (booking.Rating.HasValue)
            {
                TempData["ErrorMessage"] =
                    "You have already rated this service.";

                return RedirectToAction("History");
            }

            if (booking.ProviderId <= 0)
            {
                TempData["ErrorMessage"] =
                    "Provider information is missing.";

                return RedirectToAction("History");
            }

            if (providerId != booking.ProviderId)
            {
                TempData["ErrorMessage"] =
                    "Invalid provider for this booking.";

                return RedirectToAction("History");
            }

            var provider =
                await _context.Register
                    .FirstOrDefaultAsync(
                        p => p.Id == booking.ProviderId);

            if (provider == null)
            {
                TempData["ErrorMessage"] =
                    "Provider not found.";

                return RedirectToAction("History");
            }

            booking.Rating =
                rating;

            booking.ReviewComment =
                string.IsNullOrWhiteSpace(comment)
                    ? null
                    : comment.Trim();

            double oldTotal =
                provider.Rating *
                provider.ReviewsCount;

            provider.ReviewsCount++;

            provider.Rating =
                (oldTotal + rating) /
                provider.ReviewsCount;

            _context.Booking.Update(booking);

            _context.Register.Update(provider);

            AddProviderNotification(
                provider,
                "Rating Received",
                $"{booking.CustomerName ?? "Customer"} rated your service {rating}/5 stars for booking {booking.BookingId ?? booking.Id.ToString()}.",
                "Rating",
                "fa-solid fa-star",
                booking.Id,
                booking.BookingId,
                Url.Action("History", "Provider"));

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] =
                "Thank you! Your rating has been submitted successfully.";

            return RedirectToAction("History");
        }

        // Customer cancel booking

        [HttpGet]
        public async Task<IActionResult> CancelBooking(
            string id)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToAction(
                    "Login",
                    "Home");
            }

            if (!int.TryParse(
                id,
                out int bookingId))
            {
                return BadRequest();
            }

            var booking =
                await _context.Booking
                    .FirstOrDefaultAsync(b =>
                        b.Id == bookingId &&
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

            if (booking == null)
                return NotFound();

            // Already closed

            if (IsFinalBookingStatus(booking.Status) ||
                IsStatus(
                    booking.Status,
                    "Completed"))
            {
                return RedirectToAction(
                    "CusDashboard");
            }

            // Only pending / accepted

            if (!IsStatus(booking.Status, "Pending") &&
                !IsStatus(booking.Status, "Accepted"))
            {
                TempData["ErrorMessage"] =
                    "This booking cannot be cancelled.";

                return RedirectToAction(
                    "CusDashboard");
            }

            // Cancellation rule
            // Pending  -> anytime.
            // Accepted -> only before the two-hour cutoff.
            bool cancellationAllowed = IsCancellationAllowed(booking);

            if (!cancellationAllowed)
            {
                TempData["ErrorMessage"] =
                    IsStatus(booking.Status, "Accepted")
                        ? "Accepted bookings can only be cancelled until 2 hours before the booking."
                        : "This booking cannot be cancelled.";

                return RedirectToAction("CusDashboard");
            }

            // Refund commission for customer cancellation
            // Only an Accepted booking can have a deducted commission.
            // The existing IsCancellationAllowed() check above guarantees
            // that an Accepted booking is still more than 2 hours away.
            // Refund only once, even if the cancellation endpoint is called
            // again.

            if (IsStatus(booking.Status, "Accepted") &&
                booking.WalletCommissionDeducted &&
                booking.ProviderId > 0)
            {
                var provider = await _context.Register
                    .FirstOrDefaultAsync(p => p.Id == booking.ProviderId);

                if (provider != null)
                {
                    decimal commission = BookingCommission;

                    provider.WalletBalance += commission;
                    booking.WalletCommissionDeducted = false;
                }
            }

            // Cancel

            booking.Status =
                "Cancelled";

            // Store the exact cancellation date/time so History can show it.
            booking.CancelledAt = DateTime.Now;

            booking.CancelledBy =
                "Customer";

            booking.IsRescheduledByCustomer =
                false;

            booking.IsRescheduledByProvider =
                false;

            booking.RescheduleRequestedBy =
                null;

            booking.RequestedDate =
                null;

            booking.RequestedTime =
                null;

            booking.RescheduledStatus =
                "Cancelled";

            _context.Booking.Update(booking);

            if (booking.ProviderId > 0)
            {
                var provider = await _context.Register
                    .FirstOrDefaultAsync(p => p.Id == booking.ProviderId);

                if (provider != null)
                {
                    AddProviderNotification(
                        provider,
                        "Booking Cancelled",
                        $"{booking.CustomerName ?? "Customer"} cancelled booking {booking.BookingId ?? booking.Id.ToString()}.",
                        "Cancellation",
                        "fa-solid fa-calendar-xmark",
                        booking.Id,
                        booking.BookingId,
                        Url.Action("Dashboard", "Provider"));
                }
            }

            await _context.SaveChangesAsync();

            TempData["CustomerSuccessMessage"] =
                "Booking cancelled successfully.";

            return RedirectToAction(
                "CusDashboard");
        }

        // Customer profile GET

        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToAction(
                    "Login",
                    "Home");
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());

            if (customer == null)
                return NotFound();

            var viewModel =
                new CustomerProfileViewModel
                {
                    CustomerId =
                        customer.CustomerId ??
                        "CUS-" + customer.Id,

                    Name =
                        customer.Name ?? string.Empty,

                    Email =
                        customer.Email ?? email,

                    Phone =
                        customer.Phone ?? string.Empty,

                    City =
                        customer.City ?? string.Empty,

                    Address =
                        customer.Address ?? string.Empty,

                    ProfileImage =
                        customer.ProfileImage,

                    JoinedDate =
                        "Member since " +
                        "your ServiceGo account",

                    Latitude =
                        customer.Latitude,

                    Longitude =
                        customer.Longitude
                };

            return View(viewModel);
        }

        // Customer profile update

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(
            CustomerProfileViewModel model,
            IFormFile? profileImageFile)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message = "Your session has expired. Please login again."
                    });
                }

                return RedirectToAction(
                    "Login",
                    "Home");
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());

            if (customer == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Customer account not found."
                    });
                }

                return NotFound();
            }

            // Name and Email already come from Signup.
            // They remain protected account fields here.
            if (string.IsNullOrWhiteSpace(customer.Name))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Name is required."
                    });
                }

                TempData["ErrorMessage"] = "Name is required.";
                return RedirectToAction("CusDashboard");
            }

            if (string.IsNullOrWhiteSpace(customer.Email))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Email is required."
                    });
                }

                TempData["ErrorMessage"] = "Email is required.";
                return RedirectToAction("CusDashboard");
            }

            string phone = model.Phone?.Trim() ?? string.Empty;
            string city = model.City?.Trim() ?? string.Empty;
            string address = model.Address?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(phone))
            {
                return ProfileValidationFailure(
                    "Phone number is required.",
                    model);
            }

            if (string.IsNullOrWhiteSpace(city))
            {
                return ProfileValidationFailure(
                    "City is required.",
                    model);
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                return ProfileValidationFailure(
                    "Address is required.",
                    model);
            }

            // Profile image

            if (profileImageFile != null &&
                profileImageFile.Length > 0)
            {
                var extension =
                    Path.GetExtension(
                        profileImageFile.FileName)
                    .ToLowerInvariant();

                var allowedExtensions =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ".jpg",
                        ".jpeg",
                        ".png",
                        ".webp"
                    };

                if (!allowedExtensions.Contains(extension))
                {
                    return ProfileValidationFailure(
                        "Profile image must be JPG, JPEG, PNG or WEBP.",
                        model);
                }

                if (profileImageFile.Length > 5 * 1024 * 1024)
                {
                    return ProfileValidationFailure(
                        "Profile image must be 5 MB or smaller.",
                        model);
                }

                var uploadsFolder =
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "wwwroot",
                        "uploads",
                        "customer-profiles");

                if (!Directory.Exists(uploadsFolder))
                    Directory.CreateDirectory(uploadsFolder);

                var uniqueFileName =
                    Guid.NewGuid().ToString("N") +
                    extension;

                var filePath =
                    Path.Combine(
                        uploadsFolder,
                        uniqueFileName);

                using (var stream =
                       new FileStream(
                           filePath,
                           FileMode.Create))
                {
                    await profileImageFile.CopyToAsync(stream);
                }

                customer.ProfileImage =
                    "/uploads/customer-profiles/" +
                    uniqueFileName;
            }

            // Customer ID is assigned at email verification and never depends on profile completion.
            if (string.IsNullOrWhiteSpace(customer.CustomerId))
            {
                customer.CustomerId = $"CUS-{customer.Id}";
            }

            customer.Phone = phone;
            customer.City = city;
            customer.Address = address;

            // Joined date: saved automatically in the database the first time
            // the customer completes / updates the profile. It is never
            // overwritten on later updates.
            if (customer.JoinedDate == null)
            {
                customer.JoinedDate = DateTime.Now;
            }

            // Latitude / Longitude are read straight from the posted form with
            // InvariantCulture, so they are saved even if model binding fails.
            // If Latitude/Longitude in CustomerSignup are decimal?,
            // change (double) to (decimal) below.
            var latRaw = Request.Form["Latitude"].ToString().Trim();
            var lngRaw = Request.Form["Longitude"].ToString().Trim();

            if (double.TryParse(latRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat) &&
                double.TryParse(lngRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLng) &&
                parsedLat >= -90 && parsedLat <= 90 &&
                parsedLng >= -180 && parsedLng <= 180)
            {
                customer.Latitude = parsedLat;
                customer.Longitude = parsedLng;
            }
            else
            {
                return ProfileValidationFailure(
                    "Please select your location on the map (search address, click on map, or use My Location).",
                    model);
            }

            _context.CustomerSignup.Update(customer);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                var errorMessage =
                    ex.InnerException?.Message ??
                    ex.Message;

                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return StatusCode(
                        500,
                        new
                        {
                            success = false,
                            message =
                                "Profile database mein save nahi ho saki: " +
                                errorMessage
                        });
                }

                TempData["ErrorMessage"] =
                    "Profile database mein save nahi ho saki.";

                return RedirectToAction("CusDashboard");
            }

            // Keep the current login session synchronized.
            HttpContext.Session.SetString(
                "UserEmail",
                customer.Email);

            HttpContext.Session.SetString(
                "UserCode",
                customer.CustomerId ?? string.Empty);

            var isAjax =
                string.Equals(
                    Request.Headers["X-Requested-With"],
                    "XMLHttpRequest",
                    StringComparison.OrdinalIgnoreCase);

            if (isAjax)
            {
                return Json(new
                {
                    success = true,
                    message = "Your profile has been completed successfully.",
                    profile = new
                    {
                        name = customer.Name,
                        email = customer.Email,
                        phone = customer.Phone,
                        city = customer.City,
                        address = customer.Address,
                        profileImage = customer.ProfileImage,
                        latitude = customer.Latitude,
                        longitude = customer.Longitude
                    }
                });
            }

            TempData["SuccessMessage"] =
                "Profile updated successfully!";

            return RedirectToAction("Profile");
        }

        // Customer profile validation failure

        private IActionResult ProfileValidationFailure(
            string message,
            CustomerProfileViewModel model)
        {
            var isAjax =
                string.Equals(
                    Request.Headers["X-Requested-With"],
                    "XMLHttpRequest",
                    StringComparison.OrdinalIgnoreCase);

            if (isAjax)
            {
                return BadRequest(new
                {
                    success = false,
                    message = message
                });
            }

            TempData["ErrorMessage"] = message;
            return RedirectToAction("Profile");
        }

        // Customer messages
        // Customer messaging ID helpers

        private async Task<Register?> ResolveProviderForMessagingAsync(string? providerCode)
        {
            if (string.IsNullOrWhiteSpace(providerCode))
                return null;

            string code = providerCode.Trim();
            string numericCode = code;

            if (numericCode.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase))
                numericCode = numericCode.Substring(4).Trim();
            else if (numericCode.StartsWith("PRO", StringComparison.OrdinalIgnoreCase))
                numericCode = numericCode.Substring(3).Trim();

            if (int.TryParse(numericCode, out int providerDbId))
            {
                var provider = await _context.Register
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == providerDbId);

                if (provider != null)
                    return provider;
            }

            return await _context.Register
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    !string.IsNullOrWhiteSpace(p.ProviderId) &&
                    p.ProviderId.Trim() == code);
        }

        private static List<string> GetProviderMessageIds(Register provider)
        {
            var ids = new List<string>
            {
                provider.Id.ToString(),
                "PRO-" + provider.Id,
                "PRO" + provider.Id
            };

            if (!string.IsNullOrWhiteSpace(provider.ProviderId))
                ids.Add(provider.ProviderId.Trim());

            return ids
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Messages(
            string? providerId,
            string? providerName)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(code) &&
                !userId.HasValue)
                return RedirectToAction("Login", "Home");

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email.Trim().ToLower() == email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(code))
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c => c.CustomerId == code);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            ViewBag.CustomerProfileImage = customer.ProfileImage;
            ViewBag.UnreadNotificationCount = await GetCustomerUnreadNotificationCountAsync(customer);

            var customerMessageIds = new List<string>
            {
                customer.Id.ToString(),
                "CUS-" + customer.Id,
                "CUS" + customer.Id
            };

            if (!string.IsNullOrWhiteSpace(customer.CustomerId))
                customerMessageIds.Add(customer.CustomerId.Trim());

            if (!string.IsNullOrWhiteSpace(email))
                customerMessageIds.Add(email.Trim());

            if (!string.IsNullOrWhiteSpace(code))
                customerMessageIds.Add(code.Trim());

            customerMessageIds = customerMessageIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allMessages = await _context.Messages
                .Where(m =>
                    customerMessageIds.Contains(m.SenderId) ||
                    customerMessageIds.Contains(m.ReceiverId))
                .Where(m =>
                    !m.DeletedForEveryone &&
                    // Customer id "1" and provider id "1" can be identical, so the
                    // customer's side is decided by IsFromCustomer, not by comparing ids.
                    (m.IsFromCustomer ? !m.DeletedForSender : !m.DeletedForReceiver))
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            var rawProviderIds = allMessages
                // IsFromCustomer decides who the other person is. Comparing ids is
                // wrong here: customer id "1" and provider id "1" look identical,
                // which made provider-only chats disappear from the chat list.
                .Select(m => m.IsFromCustomer ? m.ReceiverId : m.SenderId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var providerCodes = new List<string>();
            foreach (var rawId in rawProviderIds)
            {
                var provider = await ResolveProviderForMessagingAsync(rawId);
                var canonical = provider != null
                    ? provider.Id.ToString()
                    : rawId!.Trim();

                if (!providerCodes.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                    providerCodes.Add(canonical);
            }

            var selectedProvider = await ResolveProviderForMessagingAsync(providerId);
            var selectedProviderIds = selectedProvider != null
                ? GetProviderMessageIds(selectedProvider)
                : (!string.IsNullOrWhiteSpace(providerId)
                    ? new List<string> { providerId.Trim() }
                    : new List<string>());

            if (selectedProvider != null &&
                !providerCodes.Contains(selectedProvider.Id.ToString(), StringComparer.OrdinalIgnoreCase) &&
                allMessages.Any(m =>
                    (!m.IsFromCustomer &&
                     selectedProviderIds.Contains(m.SenderId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) ||
                    (m.IsFromCustomer &&
                     selectedProviderIds.Contains(m.ReceiverId ?? string.Empty, StringComparer.OrdinalIgnoreCase))))
            {
                providerCodes.Add(selectedProvider.Id.ToString());
            }

            var chatsData = new List<object>();

            foreach (var canonicalProviderId in providerCodes)
            {
                var provider = await ResolveProviderForMessagingAsync(canonicalProviderId);
                if (provider == null)
                    continue;

                var providerIdsForChat = GetProviderMessageIds(provider);

                var chatMessages = allMessages
                    .Where(m =>
                        (m.IsFromCustomer &&
                         customerMessageIds.Contains(m.SenderId) &&
                         providerIdsForChat.Contains(m.ReceiverId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) ||
                        (!m.IsFromCustomer &&
                         providerIdsForChat.Contains(m.SenderId ?? string.Empty, StringComparer.OrdinalIgnoreCase) &&
                         customerMessageIds.Contains(m.ReceiverId)))
                    .Where(m => !m.DeletedForEveryone &&
                        (m.IsFromCustomer ? !m.DeletedForSender : !m.DeletedForReceiver))
                    .OrderBy(m => m.Timestamp)
                    .Select(m => new
                    {
                        id = m.MessageId,
                        type = m.IsFromCustomer ? "sent" : "received",
                        text = m.TextContent ?? string.Empty,
                        time = m.Timestamp.ToString("hh:mm tt")
                    })
                    .ToList();

                chatsData.Add(new
                {
                    ProviderId = provider.Id.ToString(),
                    ProviderName = provider.Name ?? "Provider",
                    ProviderProfileImage = provider.ProfilePicPath ?? string.Empty,
                    Messages = chatMessages
                });
            }

            ViewBag.Chats = chatsData;
            ViewBag.ActiveProviderId = selectedProvider?.Id.ToString() ?? string.Empty;

            string activeProviderName = "Select a chat";
            string activeProviderProfileImage = string.Empty;
            var activeMessages = new List<Message>();

            if (selectedProvider != null)
            {
                var ids = GetProviderMessageIds(selectedProvider);

                activeMessages = allMessages
                    .Where(m =>
                        (m.IsFromCustomer &&
                         customerMessageIds.Contains(m.SenderId) &&
                         ids.Contains(m.ReceiverId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) ||
                        (!m.IsFromCustomer &&
                         ids.Contains(m.SenderId ?? string.Empty, StringComparer.OrdinalIgnoreCase) &&
                         customerMessageIds.Contains(m.ReceiverId)))
                    .OrderBy(m => m.Timestamp)
                    .ToList();

                activeProviderName = selectedProvider.Name ?? "Provider";
                activeProviderProfileImage = selectedProvider.ProfilePicPath ?? string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(providerName))
            {
                activeProviderName = Uri.UnescapeDataString(providerName);
            }

            ViewBag.ActiveProviderName = activeProviderName;
            ViewBag.ActiveProviderProfileImage = activeProviderProfileImage;

            return View(activeMessages);
        }

        // Send text message

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendMessage(
            string providerId,
            string messageText)
        {
            var email =
                HttpContext.Session.GetString(
                    "UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return Json(new
                {
                    status = "error",
                    message = "Unauthorized"
                });
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email == email);

            if (customer == null ||
                string.IsNullOrWhiteSpace(providerId) ||
                string.IsNullOrWhiteSpace(messageText))
            {
                return Json(new
                {
                    status = "error",
                    message = "Invalid data"
                });
            }

            if (!int.TryParse(
                providerId,
                out int providerIdNumber))
            {
                return Json(new
                {
                    status = "error",
                    message = "Invalid provider."
                });
            }

            var provider =
                await _context.Register
                    .FirstOrDefaultAsync(
                        p => p.Id ==
                             providerIdNumber);

            if (provider == null)
            {
                return Json(new
                {
                    status = "error",
                    message = "Provider not found."
                });
            }

            string customerId =
                !string.IsNullOrWhiteSpace(
                    customer.CustomerId)
                    ? customer.CustomerId
                    : customer.Id.ToString();

            var cleanMessage =
                messageText.Trim();

            var newMessage =
                new Message
                {
                    SenderId =
                        customerId,

                    ReceiverId =
                        providerId,

                    SenderName =
                        customer.Name ??
                        customer.Email,

                    TextContent =
                        cleanMessage,

                    IsFromCustomer =
                        true,

                    Timestamp =
                        DateTime.Now
                };

            _context.Messages.Add(newMessage);

            // Save first so the database-generated MessageId is available
            // for the provider notification reference.
            await _context.SaveChangesAsync();

            AddProviderNotification(
                provider,
                "New Message",
                $"{customer.Name ?? "Customer"} sent you a new message.",
                "Message",
                "fa-solid fa-message",
                null,
                newMessage.MessageId.ToString(),
                Url.Action("Messages", "Provider"));

            await _context.SaveChangesAsync();

            return Json(new
            {
                status = "success",

                id =
                    newMessage.MessageId,

                text =
                    cleanMessage,

                time =
                    newMessage.Timestamp
                        .ToString("hh:mm tt")
            });
        }

        // Send audio message

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendAudioMessage(
            string providerId,
            IFormFile audioFile)
        {
            var email =
                HttpContext.Session.GetString(
                    "UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return Json(new
                {
                    status = "error",
                    message = "Unauthorized"
                });
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email == email);

            if (customer == null ||
                string.IsNullOrWhiteSpace(providerId) ||
                audioFile == null ||
                audioFile.Length == 0)
            {
                return Json(new
                {
                    status = "error",
                    message = "Invalid data"
                });
            }

            if (!int.TryParse(
                providerId,
                out int providerIdNumber))
            {
                return Json(new
                {
                    status = "error",
                    message = "Invalid provider."
                });
            }

            var provider =
                await _context.Register
                    .FirstOrDefaultAsync(
                        p => p.Id ==
                             providerIdNumber);

            if (provider == null)
            {
                return Json(new
                {
                    status = "error",
                    message = "Provider not found."
                });
            }

            var uploadsFolder =
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "wwwroot",
                    "uploads");

            if (!Directory.Exists(
                uploadsFolder))
            {
                Directory.CreateDirectory(
                    uploadsFolder);
            }

            var extension =
                Path.GetExtension(
                    audioFile.FileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".webm";
            }

            var uniqueFileName =
                Guid.NewGuid().ToString() +
                extension.ToLowerInvariant();

            var filePath =
                Path.Combine(
                    uploadsFolder,
                    uniqueFileName);

            using (var stream =
                   new FileStream(
                       filePath,
                       FileMode.Create))
            {
                await audioFile.CopyToAsync(
                    stream);
            }

            var relativeUrl =
                "/uploads/" +
                uniqueFileName;

            string customerId =
                !string.IsNullOrWhiteSpace(
                    customer.CustomerId)
                    ? customer.CustomerId
                    : customer.Id.ToString();

            var audioMessage =
                new Message
                {
                    SenderId =
                        customerId,

                    ReceiverId =
                        providerId,

                    SenderName =
                        customer.Name ??
                        customer.Email,

                    TextContent =
                        relativeUrl,

                    AudioUrl =
                        relativeUrl,

                    IsFromCustomer =
                        true,

                    Timestamp =
                        DateTime.Now
                };

            _context.Messages.Add(audioMessage);

            // Save first so the generated MessageId can be referenced by the
            // provider notification.
            await _context.SaveChangesAsync();

            AddProviderNotification(
                provider,
                "New Voice Message",
                $"{customer.Name ?? "Customer"} sent you a voice message.",
                "Message",
                "fa-solid fa-microphone",
                null,
                audioMessage.MessageId.ToString(),
                Url.Action("Messages", "Provider"));

            await _context.SaveChangesAsync();

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

        // Delete message
        // Supports WhatsApp-style:
        // 1. Delete for me       -> hides only on customer's side.
        // 2. Delete for everyone -> sender can hide it for both sides.
        // The Message row is NEVER deleted so notification/history
        // references remain valid.

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMessage(
            int messageId,
            string deleteType)
        {
            var email =
                HttpContext.Session.GetString(
                    "UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return Json(new
                {
                    status = "error",
                    message = "Unauthorized"
                });
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());

            if (customer == null)
            {
                return Json(new
                {
                    status = "error",
                    message = "Customer not found"
                });
            }

            string customerId =
                !string.IsNullOrWhiteSpace(
                    customer.CustomerId)
                    ? customer.CustomerId
                    : customer.Id.ToString();

            deleteType =
                (deleteType ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            if (deleteType != "me" &&
                deleteType != "everyone")
            {
                return Json(new
                {
                    status = "error",
                    message = "Invalid delete option."
                });
            }

            // Customer can delete a message for themselves whether they
            // sent it or received it. Never physically remove the DB row.
            // Every id format this customer can be stored under.
            var customerIdsForMessage = new List<string>
            {
                customer.Id.ToString(),
                "CUS-" + customer.Id,
                "CUS" + customer.Id,
                customerId
            };

            if (!string.IsNullOrWhiteSpace(customer.CustomerId))
                customerIdsForMessage.Add(customer.CustomerId.Trim());

            if (!string.IsNullOrWhiteSpace(customer.Email))
                customerIdsForMessage.Add(customer.Email.Trim());

            var msg =
                await _context.Messages
                    .FirstOrDefaultAsync(m => m.MessageId == messageId);

            // The customer owns the message if they sent it, or if it was
            // received by them (IsFromCustomer decides which side is theirs,
            // because customer id "1" and provider id "1" can look identical).
            if (msg != null &&
                !((msg.IsFromCustomer &&
                   customerIdsForMessage.Contains(msg.SenderId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) ||
                  (!msg.IsFromCustomer &&
                   customerIdsForMessage.Contains(msg.ReceiverId ?? string.Empty, StringComparer.OrdinalIgnoreCase))))
            {
                msg = null;
            }

            if (msg == null)
            {
                return Json(new
                {
                    status = "error",
                    message =
                        "Message not found or you are not allowed to delete it."
                });
            }

            // Sent by the customer = "Delete for Me" + "Delete for Everyone".
            // Received from the provider = "Delete for Me" only.
            bool isSender = msg.IsFromCustomer;

            // Delete for everyone
            if (deleteType == "everyone")
            {
                // Only the original sender may delete for everyone.
                if (!isSender)
                {
                    return Json(new
                    {
                        status = "error",
                        message =
                            "Only the sender can delete this message for everyone."
                    });
                }

                if (msg.DeletedForEveryone)
                {
                    return Json(new
                    {
                        status = "success",
                        deleteType = "everyone",
                        alreadyDeleted = true,
                        messageId = msg.MessageId
                    });
                }

                msg.DeletedForEveryone = true;
                msg.DeletedForSender = true;
                msg.DeletedForReceiver = true;

                // Save the deletion state first. This keeps the DB record
                // available for notifications and prevents broken history.
                string? audioUrlToDelete = msg.AudioUrl;

                await _context.SaveChangesAsync();

                // Audio is no longer needed after Delete for Everyone.
                DeleteMessageAudioFile(audioUrlToDelete);

                return Json(new
                {
                    status = "success",
                    deleteType = "everyone",
                    messageId = msg.MessageId
                });
            }

            // Delete for me
            if (isSender)
            {
                if (msg.DeletedForSender)
                {
                    return Json(new
                    {
                        status = "success",
                        deleteType = "me",
                        alreadyDeleted = true,
                        messageId = msg.MessageId
                    });
                }

                msg.DeletedForSender = true;
            }
            else
            {
                if (msg.DeletedForReceiver)
                {
                    return Json(new
                    {
                        status = "success",
                        deleteType = "me",
                        alreadyDeleted = true,
                        messageId = msg.MessageId
                    });
                }

                msg.DeletedForReceiver = true;
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                status = "success",
                deleteType = "me",
                messageId = msg.MessageId
            });
        }

        // Delete chat for customer
        // Hides the complete conversation only from the logged-in
        // customer. The provider's side remains unchanged.

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteChat(string? providerId)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(code) &&
                !userId.HasValue)
            {
                return Unauthorized(new
                {
                    status = "error",
                    message = "Session expired. Please login again."
                });
            }

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email.Trim().ToLower() ==
                        email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(code))
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c => c.CustomerId == code);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
            {
                return Unauthorized(new
                {
                    status = "error",
                    message = "Customer account not found."
                });
            }

            if (string.IsNullOrWhiteSpace(providerId))
            {
                return BadRequest(new
                {
                    status = "error",
                    message = "Provider is not selected."
                });
            }

            // Resolve the selected provider first so 1, PRO-1 and the
            // provider's stored ProviderId all refer to the same chat.
            string selectedProviderCode = providerId.Trim();
            string numericProviderCode = selectedProviderCode;

            if (numericProviderCode.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase))
                numericProviderCode = numericProviderCode[4..].Trim();
            else if (numericProviderCode.StartsWith("PRO", StringComparison.OrdinalIgnoreCase))
                numericProviderCode = numericProviderCode[3..].Trim();

            Register? provider = null;

            if (int.TryParse(numericProviderCode, out int providerDbId))
            {
                provider = await _context.Register
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == providerDbId);
            }
            else
            {
                provider = await _context.Register
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == selectedProviderCode);
            }

            var providerIdsForDelete = new List<string>
          {
              selectedProviderCode,
              numericProviderCode
          };

            if (provider != null)
            {
                providerIdsForDelete.Add(provider.Id.ToString());
                providerIdsForDelete.Add("PRO-" + provider.Id);
                providerIdsForDelete.Add("PRO" + provider.Id);

                if (!string.IsNullOrWhiteSpace(provider.ProviderId))
                    providerIdsForDelete.Add(provider.ProviderId.Trim());
            }

            providerIdsForDelete = providerIdsForDelete
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var customerIdsForDelete = new List<string>
          {
              customer.Id.ToString()
          };

            if (!string.IsNullOrWhiteSpace(customer.CustomerId))
            {
                customerIdsForDelete.Add(customer.CustomerId.Trim());

                string numericCustomerCode = customer.CustomerId.Trim();
                if (numericCustomerCode.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                    numericCustomerCode = numericCustomerCode[4..].Trim();
                else if (numericCustomerCode.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                    numericCustomerCode = numericCustomerCode[3..].Trim();

                customerIdsForDelete.Add(numericCustomerCode);
                customerIdsForDelete.Add("CUS-" + numericCustomerCode);
            }

            customerIdsForDelete.Add("CUS-" + customer.Id);
            customerIdsForDelete.Add("CUS" + customer.Id);

            if (!string.IsNullOrWhiteSpace(email))
                customerIdsForDelete.Add(email.Trim());

            if (!string.IsNullOrWhiteSpace(code))
                customerIdsForDelete.Add(code.Trim());

            customerIdsForDelete = customerIdsForDelete
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var conversationMessages =
                await _context.Messages
                    .Where(m =>
                        (customerIdsForDelete.Contains(m.SenderId) &&
                         providerIdsForDelete.Contains(m.ReceiverId))
                        ||
                        (providerIdsForDelete.Contains(m.SenderId) &&
                         customerIdsForDelete.Contains(m.ReceiverId)))
                    .ToListAsync();

            foreach (var message in conversationMessages)
            {
                // Use IsFromCustomer: customer id "1" and provider id "1" can be the
                // same text, so comparing ids can pick the wrong side.
                bool customerIsSender = message.IsFromCustomer;

                if (customerIsSender)
                    message.DeletedForSender = true;
                else
                    message.DeletedForReceiver = true;
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                status = "success",
                message = "Chat deleted successfully."
            });
        }
        // Delete audio file
        // This is called only after Delete-for-Everyone is saved.
        // Delete-for-Me NEVER removes the file because the other
        // participant may still need to play it.
        private void DeleteMessageAudioFile(string? audioUrl)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return;

            var relativePath =
                audioUrl
                    .TrimStart('/')
                    .Replace(
                        '/',
                        Path.DirectorySeparatorChar);

            var physicalPath =
                Path.Combine(
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
                    // Database deletion state is already saved; a failed
                    // physical-file cleanup must not break the request.
                }
            }
        }

        // Customer complaint

        [HttpGet]
        public async Task<IActionResult> Complaint()
        {
            var email = HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
                return RedirectToAction("Login", "Home");

            var customer = await _context.CustomerSignup
                .FirstOrDefaultAsync(c => c.Email == email);

            if (customer == null)
                return RedirectToAction("Login", "Home");

            var lastComplaint = await _context.Complaints
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync();

            int nextId = lastComplaint != null
                ? lastComplaint.Id + 1
                : 1;

            ViewBag.NextComplaintId = $"COMP-{nextId}";

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Complaint(
            ComplaintModel model,
            string? OtherIssueText)
        {
            var email =
                HttpContext.Session.GetString("UserEmail");

            bool isAjax =
                Request.Headers["X-Requested-With"]
                    .ToString()
                    .Equals(
                        "XMLHttpRequest",
                        StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(email))
            {
                if (isAjax)
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message =
                            "Your session has expired. Please login again."
                    });
                }

                return RedirectToAction(
                    "Login",
                    "Home");
            }

            var customer =
                await _context.CustomerSignup
                    .FirstOrDefaultAsync(
                        c => c.Email == email);

            if (customer == null)
            {
                if (isAjax)
                {
                    return NotFound(new
                    {
                        success = false,
                        message =
                            "Customer account not found."
                    });
                }

                return RedirectToAction(
                    "Login",
                    "Home");
            }

            // One complaint per booking. Enforced server-side as well as in the UI.
            var submittedComplaintBookingId = Request.Form["BookingId"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(submittedComplaintBookingId))
            {
                var bookingIdProperty = typeof(ComplaintModel).GetProperty("BookingId");
                if (bookingIdProperty != null)
                {
                    var customerComplaints = await _context.Complaints
                        .AsNoTracking()
                        .Where(c => c.CustomerEmail == email)
                        .ToListAsync();

                    bool duplicateComplaint = customerComplaints.Any(c =>
                        string.Equals(
                            Convert.ToString(bookingIdProperty.GetValue(c)),
                            submittedComplaintBookingId,
                            StringComparison.OrdinalIgnoreCase));

                    if (duplicateComplaint)
                    {
                        if (isAjax)
                            return Conflict(new { success = false, message = "You can file only one complaint for this booking." });

                        TempData["ErrorMessage"] = "You can file only one complaint for this booking.";
                        return RedirectToAction("CusDashboard");
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
                        b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}"));

                if (complaintBooking == null ||
                    !string.Equals(complaintBooking.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (isAjax)
                        return BadRequest(new { success = false, message = "A complaint can only be filed for a completed booking." });

                    TempData["ErrorMessage"] = "A complaint can only be filed for a completed booking.";
                    return RedirectToAction("CusDashboard");
                }
            }
            else
            {
                if (isAjax)
                    return BadRequest(new { success = false, message = "Valid booking ID is required for a complaint." });

                TempData["ErrorMessage"] = "Valid booking ID is required for a complaint.";
                return RedirectToAction("CusDashboard");
            }

            model.CustomerId =
                "CUS-" + customer.Id;

            model.CustomerName =
                customer.Name ?? "Unknown Customer";

            model.CustomerEmail =
                customer.Email ?? email;

            if (string.Equals(
                model.OtherIssue,
                "Other",
                StringComparison.OrdinalIgnoreCase))
            {
                model.OtherIssue =
                    string.IsNullOrWhiteSpace(
                        OtherIssueText)
                        ? "Other"
                        : OtherIssueText.Trim();
            }
            else
            {
                model.OtherIssue =
                    model.OtherIssue?.Trim() ?? "";
            }

            // The current complaint form sends the selected issue in
            // OtherIssue, but does not send IssueCategory. Map the
            // selected issue to a meaningful category instead of saving
            // "General" for every complaint.
            if (string.IsNullOrWhiteSpace(model.IssueCategory) ||
                model.IssueCategory.Equals(
                    "General",
                    StringComparison.OrdinalIgnoreCase))
            {
                model.IssueCategory =
                    model.OtherIssue switch
                    {
                        "Late Arrival" => "Service Issue",
                        "Service Not Completed" => "Service Issue",
                        "Overcharging" => "Payment Issue",
                        "Unprofessional Behavior" => "Provider Conduct",
                        "Other" => "Other",
                        _ => string.IsNullOrWhiteSpace(model.OtherIssue)
                            ? "General"
                            : model.OtherIssue.Trim()
                    };
            }
            else
            {
                model.IssueCategory =
                    model.IssueCategory.Trim();
            }

            model.ComplaintDetails =
                string.IsNullOrWhiteSpace(
                    model.ComplaintDetails)
                    ? "No details provided."
                    : model.ComplaintDetails.Trim();

            model.ProviderName =
                string.IsNullOrWhiteSpace(
                    model.ProviderName)
                    ? "Not Assigned"
                    : model.ProviderName.Trim();

            model.CreatedAt =
                DateTime.Now;

            try
            {
                model.ComplaintId =
                    "TEMP-" +
                    Guid.NewGuid()
                        .ToString("N");

                _context.Complaints.Add(model);

                await _context.SaveChangesAsync();

                model.ComplaintId =
                    $"COMP-{model.Id}";

                _context.Complaints.Update(model);

                if (!string.IsNullOrWhiteSpace(model.ProviderId))
                {
                    string providerIdText = model.ProviderId
                        .Replace("PRO-", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();

                    Register? provider = null;

                    if (int.TryParse(providerIdText, out int providerDbId))
                    {
                        provider = await _context.Register
                            .FirstOrDefaultAsync(p => p.Id == providerDbId);
                    }

                    if (provider == null)
                    {
                        provider = await _context.Register
                            .FirstOrDefaultAsync(p =>
                                p.ProviderId == model.ProviderId ||
                                p.ProviderId == providerIdText);
                    }

                    if (provider != null)
                    {
                        AddProviderNotification(
                            provider,
                            "Complaint Received",
                            $"A customer has submitted a complaint against your account. Complaint ID: {model.ComplaintId}.",
                            "Complaint",
                            "fa-solid fa-triangle-exclamation",
                            complaintBookingDbId,
                            model.ComplaintId,
                            Url.Action("History", "Provider"));
                    }
                }

                // Complaint must also reach Admin.
                // The customer does not receive a notification for their own complaint.
                _context.Notifications.Add(new Notification
                {
                    UserId = "Admin",
                    TargetUserId = "Admin",
                    SenderUserId = customer.Id.ToString(),
                    Title = "New Customer Complaint",
                    Message = $"Customer {model.CustomerName ?? "Customer"} has submitted complaint {model.ComplaintId} against a provider.",
                    NotificationType = "Complaint",
                    IconClass = "fa-solid fa-triangle-exclamation",
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    BookingId = complaintBookingDbId,
                    ReferenceId = model.ComplaintId,
                    ActionUrl = Url.Action("Complaint", "Admin")
                });

                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                var errorMessage =
                    ex.InnerException?.Message
                    ?? ex.Message;

                if (isAjax)
                {
                    return StatusCode(
                        500,
                        new
                        {
                            success = false,
                            message =
                                "Complaint database mein save nahi ho saki: "
                                + errorMessage
                        });
                }

                TempData["ErrorMessage"] =
                    "Complaint save nahi ho saki: "
                    + errorMessage;

                return RedirectToAction(
                    "CusDashboard");
            }
            catch (Exception ex)
            {
                if (isAjax)
                {
                    return StatusCode(
                        500,
                        new
                        {
                            success = false,
                            message =
                                "Complaint submit karte waqt error aa gaya: "
                                + ex.Message
                        });
                }

                TempData["ErrorMessage"] =
                    "Complaint submit karte waqt error aa gaya: "
                    + ex.Message;

                return RedirectToAction(
                    "CusDashboard");
            }

            if (isAjax)
            {
                return Ok(new
                {
                    success = true,

                    message =
                        "Aapki complaint kamyabi se submit ho gayi hai!",

                    complaintId =
                        model.ComplaintId,

                    customerId =
                        model.CustomerId,

                    createdAt =
                        model.CreatedAt.ToString(
                            "dd MMM yyyy hh:mm tt")
                });
            }

            TempData["SuccessMessage"] =
                "Aapki complaint kamyabi se submit ho gayi hai!";

            return RedirectToAction(
                "CusDashboard");
        }

        // Customer notification click policy
        // Where a clickable customer notification opens.
        private enum CustomerNotificationTarget
        {
            None,       // view only, not clickable
            Messages,   // customer's own Messages page
            Pending,    // Dashboard -> Pending tab
            Accepted,   // Dashboard -> Accepted tab
            Completed,  // Dashboard -> Completed tab
            History     // Job History page (cancelled / rejected / expired bookings)
        }

        private static bool IsRejectedRescheduleNotification(Notification n)
        {
            var text = ((n.Title ?? string.Empty) + " " + (n.NotificationType ?? string.Empty))
                .ToLowerInvariant();

            return text.Contains("reschedule") && text.Contains("reject");
        }

        // Customer notification rules:
        //   Cancel booking ............. clickable -> History
        //   Reschedule request ......... clickable -> Pending (or Accepted if the booking is accepted)
        //   Complaint / Warning ........ NOT clickable (complaint filed, admin warning, provider side)
        //   Reschedule accepted ........ clickable -> Accepted
        //   Reschedule rejected ........ clickable -> Accepted
        //   Booking accepted ........... clickable -> Accepted
        //   Booking rejected ........... clickable -> History
        //   Message .................... clickable -> customer Messages page
        //   Booking completed .......... clickable -> Completed
        //   Anything else .............. NOT clickable
        //
        // A booking notification is only clickable while the booking is still in the
        // state it describes (see IsCustomerNotificationCurrent). Older notifications
        // become view only once the booking has moved on.
        private static CustomerNotificationTarget GetCustomerNotificationTarget(Notification n)
        {
            var title = (n.Title ?? string.Empty).Trim().ToLowerInvariant();
            var type = (n.NotificationType ?? string.Empty).Trim().ToLowerInvariant();
            var text = title + " " + type;

            // Complaint + warning notifications are always view only.
            if (text.Contains("complaint") || text.Contains("warning"))
                return CustomerNotificationTarget.None;

            // Messages open the customer's own Messages page.
            if (type.Contains("message") || title.Contains("message") || title.Contains("whatsapp"))
                return CustomerNotificationTarget.Messages;

            // Cancelled booking: kept in Job History.
            if (text.Contains("cancel"))
                return CustomerNotificationTarget.History;

            bool isReschedule = text.Contains("reschedule");

            if (text.Contains("reject"))
            {
                // Rejected reschedule opens Accepted; a rejected booking is kept in Job History.
                return isReschedule
                    ? CustomerNotificationTarget.Accepted
                    : CustomerNotificationTarget.History;
            }

            // Reschedule accepted -> Accepted.
            if (isReschedule && text.Contains("accept"))
                return CustomerNotificationTarget.Accepted;

            // Reschedule request -> Pending.
            if (isReschedule)
                return CustomerNotificationTarget.Pending;

            // Booking accepted -> Accepted.
            if (text.Contains("accept"))
                return CustomerNotificationTarget.Accepted;

            // Booking completed (mark as complete) -> Completed.
            if (text.Contains("complete"))
                return CustomerNotificationTarget.Completed;

            return CustomerNotificationTarget.None;
        }

        private static bool IsCustomerNotificationClickable(Notification n)
        {
            return GetCustomerNotificationTarget(n) != CustomerNotificationTarget.None;
        }

        // Booking state used to decide whether a notification is still current.
        private sealed record NotificationBookingState(string? Status, string? RescheduledStatus);

        // True while the booking is still in the state the notification describes.
        // Example: once a rescheduled booking is accepted, the old "reschedule request"
        // notification becomes view only and the "accepted" notification stays clickable.
        private static bool IsCustomerNotificationCurrent(
            Notification n,
            IReadOnlyDictionary<int, NotificationBookingState> bookingStates)
        {
            var target = GetCustomerNotificationTarget(n);

            // Messages and non booking notifications do not depend on a booking.
            if (target == CustomerNotificationTarget.None ||
                target == CustomerNotificationTarget.Messages ||
                !n.BookingId.HasValue)
            {
                return true;
            }

            if (!bookingStates.TryGetValue(n.BookingId.Value, out var state))
                return false;

            var status = (state.Status ?? string.Empty).Trim().ToLowerInvariant();
            var reschedule = (state.RescheduledStatus ?? string.Empty).Trim().ToLowerInvariant();

            bool isPendingStatus = status == "" || status == "pending" || status == "reschedule requested";
            bool isAcceptedStatus = status == "accepted" || status == "rescheduled";

            switch (target)
            {
                case CustomerNotificationTarget.Pending:
                    // Reschedule request: current only while it is waiting for an answer.
                    return reschedule == "pending" && (isPendingStatus || isAcceptedStatus);

                case CustomerNotificationTarget.Accepted:
                    return isAcceptedStatus;

                case CustomerNotificationTarget.Completed:
                    return status == "completed";

                case CustomerNotificationTarget.History:
                    return status == "cancelled" || status == "rejected" || status == "expired";

                default:
                    return true;
            }
        }

        // Ids that mean "this customer is the SENDER". The bare numeric id is
        // deliberately NOT included: customer id 1 and provider id 1 look the
        // same, which hid every notification a provider sent to this customer.
        private List<string> GetCustomerSenderIds(CustomerSignup customer)
        {
            var ids = new List<string> { "Customer" };

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(value.Trim());
                }
            }

            Add(customer.Email);
            Add(customer.CustomerId);
            Add("CUS-" + customer.Id);
            Add("CUS" + customer.Id);
            Add(HttpContext.Session.GetString("UserEmail"));
            Add(HttpContext.Session.GetString("UserCode"));

            return ids;
        }

        // Customer notifications

        // Notifications are shown 15 at a time. "Load more" calls
        // Notifications?take=30, then take=45, and so on.
        private const int NotificationPageSize = 15;

        [HttpGet]
        public async Task<IActionResult> Notifications(int take = NotificationPageSize)
        {
            if (take < NotificationPageSize)
                take = NotificationPageSize;

            var customerEmail =
                HttpContext.Session.GetString("UserEmail");

            var customerCode =
                HttpContext.Session.GetString("UserCode");
            var customerUserId =
                HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(customerEmail) &&
                string.IsNullOrWhiteSpace(customerCode) &&
                !customerUserId.HasValue)
            {
                return RedirectToAction("Login", "Home");
            }

            // Resolve the logged-in customer so notifications created with
            // email, customer code, numeric DB id, UserId or TargetUserId
            // can all be displayed consistently.
            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email != null && c.Email.Trim().ToLower() == customerEmail.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(customerCode))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c =>
                        c.CustomerId == customerCode);
            }

            if (customer == null && customerUserId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == customerUserId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            ViewBag.CustomerProfileImage = customer.ProfileImage;
            ViewBag.UnreadNotificationCount = await GetCustomerUnreadNotificationCountAsync(customer);

            var userIds = new List<string>();

            void AddUserId(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !userIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    userIds.Add(value.Trim());
                }
            }

            AddUserId(customer.Email);
            AddUserId(customer.CustomerId);
            AddUserId(customer.Id.ToString());
            AddUserId(customerEmail);
            AddUserId(customerCode);

            var senderIds = GetCustomerSenderIds(customer);
            var notificationQuery = _context.Notifications
                .Where(n =>
                    (userIds.Contains(n.UserId ?? string.Empty) ||
                     userIds.Contains(n.TargetUserId ?? string.Empty)) &&
                    !senderIds.Contains(n.SenderUserId ?? string.Empty));

            int totalNotificationCount = await notificationQuery.CountAsync();

            // Latest first. Only the first `take` (15, 30, 45...) are loaded.
            var notifications = await notificationQuery
                .OrderByDescending(n => n.Timestamp)
                .ThenByDescending(n => n.NotificationId)
                .Take(take)
                .ToListAsync();

            ViewBag.TotalNotificationCount = totalNotificationCount;
            ViewBag.HasMoreNotifications = totalNotificationCount > notifications.Count;
            ViewBag.NextNotificationTake = take + NotificationPageSize;

            var rejectedRescheduleBookingIds = notifications
                .Where(n =>
                    n.BookingId.HasValue &&
                    IsRejectedRescheduleNotification(n))
                .Select(n => n.BookingId!.Value)
                .Distinct()
                .ToList();

            var activeRejectedRescheduleBookingIds = rejectedRescheduleBookingIds.Count == 0
                ? new HashSet<int>()
                : (await _context.Booking
                    .AsNoTracking()
                    .Where(b =>
                        rejectedRescheduleBookingIds.Contains(b.Id) &&
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
                    IsRejectedRescheduleNotification(n))
                .Select(n => n.NotificationId)
                .ToHashSet();

            // Current status of every booking mentioned by these notifications.
            var notificationBookingIds = notifications
                .Where(n => n.BookingId.HasValue)
                .Select(n => n.BookingId!.Value)
                .Distinct()
                .ToList();

            var notificationBookingStates = notificationBookingIds.Count == 0
                ? new Dictionary<int, NotificationBookingState>()
                : await _context.Booking
                    .AsNoTracking()
                    .Where(b => notificationBookingIds.Contains(b.Id))
                    .ToDictionaryAsync(
                        b => b.Id,
                        b => new NotificationBookingState(b.Status, b.RescheduledStatus));

            // The view reads this set to decide which cards are clickable.
            ViewBag.ClickableNotificationIds = notifications
                .Where(n =>
                {
                    if (!IsCustomerNotificationClickable(n))
                        return false;

                    // Older notifications stay view only once the booking has moved on.
                    // Rejected reschedules keep their own rule below.
                    if (!IsRejectedRescheduleNotification(n) &&
                        !IsCustomerNotificationCurrent(n, notificationBookingStates))
                    {
                        return false;
                    }

                    // A rejected reschedule on a booking that is no longer
                    // active (cancelled / expired / rejected) stays view only.
                    if (IsRejectedRescheduleNotification(n) &&
                        n.BookingId.HasValue &&
                        !activeRejectedRescheduleBookingIds.Contains(n.BookingId.Value))
                    {
                        return false;
                    }

                    return true;
                })
                .Select(n => n.NotificationId)
                .ToHashSet();

            return View(notifications);
        }
        // Find the provider behind a chat notification
        private async Task<Register?> ResolveProviderFromNotificationAsync(Notification n)
        {
            // 1. The notification points to a message: the provider is that message's sender.
            if (int.TryParse(n.ReferenceId, out int messageId))
            {
                var msg = await _context.Messages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.MessageId == messageId);

                if (msg != null && !msg.IsFromCustomer)
                {
                    var fromMessage = await ResolveProviderForMessagingAsync(msg.SenderId);
                    if (fromMessage != null)
                        return fromMessage;
                }
            }

            // 2. The notification sender is the provider.
            if (!string.IsNullOrWhiteSpace(n.SenderUserId))
            {
                var fromSender = await ResolveProviderForMessagingAsync(n.SenderUserId);
                if (fromSender != null)
                    return fromSender;
            }

            // 3. ActionUrl carries ?providerId=...
            if (!string.IsNullOrWhiteSpace(n.ActionUrl))
            {
                int q = n.ActionUrl.IndexOf('?');
                if (q >= 0)
                {
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                        .ParseQuery(n.ActionUrl.Substring(q));

                    if (query.TryGetValue("providerId", out var value) &&
                        !string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        return await ResolveProviderForMessagingAsync(value.ToString());
                    }
                }
            }

            return null;
        }

        // Open customer notification
        [HttpGet]
        public async Task<IActionResult> OpenNotification(int id)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            CustomerSignup? customer = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());
            }
            if (customer == null && !string.IsNullOrWhiteSpace(code))
                customer = await _context.CustomerSignup.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == code);
            if (customer == null && userId.HasValue)
                customer = await _context.CustomerSignup.AsNoTracking().FirstOrDefaultAsync(c => c.Id == userId.Value);

            if (customer == null)
                return RedirectToAction("Login", "Home");

            var ids = new List<string>();
            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                    ids.Add(value.Trim());
            }
            Add(customer.Email); Add(customer.CustomerId); Add(customer.Id.ToString());

            var senderIds = GetCustomerSenderIds(customer);
            var notification = await _context.Notifications.FirstOrDefaultAsync(n =>
                n.NotificationId == id &&
                (ids.Contains(n.UserId ?? string.Empty) || ids.Contains(n.TargetUserId ?? string.Empty)) &&
                !senderIds.Contains(n.SenderUserId ?? string.Empty));

            if (notification == null)
                return RedirectToAction("Notifications", "Customer");

            if (!IsCustomerNotificationClickable(notification))
                return RedirectToAction("Notifications", "Customer");

            bool isRejectedReschedule =
                IsRejectedRescheduleNotification(notification);

            if (isRejectedReschedule && notification.BookingId.HasValue)
            {
                string customerCode =
                    !string.IsNullOrWhiteSpace(customer.CustomerId)
                        ? customer.CustomerId.Trim()
                        : $"CUS-{customer.Id}";

                var booking = await _context.Booking
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b =>
                        b.Id == notification.BookingId.Value &&
                        b.CustomerID == customerCode);

                bool activeBooking =
                    booking != null &&
                    (
                        string.IsNullOrWhiteSpace(booking.Status) ||
                        booking.Status == "Pending" ||
                        booking.Status == "Accepted" ||
                        booking.Status == "Reschedule Requested" ||
                        booking.Status == "Rescheduled"
                    );

                if (!activeBooking)
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Customer");
                }
            }

            // Old notification whose booking has moved on: mark as read, do not open anything.
            if (!IsRejectedRescheduleNotification(notification) &&
                notification.BookingId.HasValue)
            {
                var bookingId = notification.BookingId.Value;

                var currentState = await _context.Booking
                    .AsNoTracking()
                    .Where(b => b.Id == bookingId)
                    .Select(b => new NotificationBookingState(b.Status, b.RescheduledStatus))
                    .FirstOrDefaultAsync();

                var states = currentState == null
                    ? new Dictionary<int, NotificationBookingState>()
                    : new Dictionary<int, NotificationBookingState> { [bookingId] = currentState };

                if (!IsCustomerNotificationCurrent(notification, states))
                {
                    notification.IsRead = true;
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Notifications", "Customer");
                }
            }

            notification.IsRead = true;
            await _context.SaveChangesAsync();

            var target = GetCustomerNotificationTarget(notification);

            // Cancelled / rejected bookings are kept in Job History.
            if (target == CustomerNotificationTarget.History)
                return RedirectToAction("History", "Customer",
                    new { bookingId = notification.BookingId });

            // A reschedule request on an already accepted booking is answered in the Accepted tab.
            if (target == CustomerNotificationTarget.Pending && notification.BookingId.HasValue)
            {
                var requestedBookingId = notification.BookingId.Value;

                var requestedStatus = await _context.Booking
                    .AsNoTracking()
                    .Where(b => b.Id == requestedBookingId)
                    .Select(b => b.Status)
                    .FirstOrDefaultAsync();

                var normalizedStatus = (requestedStatus ?? string.Empty).Trim().ToLowerInvariant();

                if (normalizedStatus == "accepted" || normalizedStatus == "rescheduled")
                    target = CustomerNotificationTarget.Accepted;
            }

            // Message notifications open the customer's own Messages page.
            if (target == CustomerNotificationTarget.Messages)
            {
                // Open the exact chat of the provider who sent the message.
                var chatProvider = await ResolveProviderFromNotificationAsync(notification);

                if (chatProvider != null)
                {
                    return RedirectToAction("Messages", "Customer",
                        new { providerId = chatProvider.Id.ToString() });
                }

                return RedirectToAction("Messages", "Customer");
            }

            // Booking notifications open the matching Dashboard tab.
            string? tab = target switch
            {
                CustomerNotificationTarget.Pending => "pending",
                CustomerNotificationTarget.Accepted => "accepted",
                CustomerNotificationTarget.Completed => "completed",
                _ => null
            };

            if (tab != null)
            {
                if (notification.BookingId.HasValue)
                {
                    return RedirectToAction("CusDashboard", "Customer",
                        new { tab, bookingId = notification.BookingId.Value });
                }

                return RedirectToAction("CusDashboard", "Customer", new { tab });
            }

            return RedirectToAction("Notifications", "Customer");
        }

        // Dismiss customer notification
        // Removes only the notification record from the customer's
        // notification centre. It does NOT delete the booking, message,
        // complaint or any other underlying business record.

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DismissNotification(int id)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(code) &&
                !userId.HasValue)
            {
                return RedirectToAction("Login", "Home");
            }

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email.Trim().ToLower() == email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(code))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CustomerId == code);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            var ids = new List<string>();

            void AddCustomerId(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(value.Trim());
                }
            }

            AddCustomerId(customer.Email);
            AddCustomerId(customer.CustomerId);
            AddCustomerId(customer.Id.ToString());

            var senderIds = GetCustomerSenderIds(customer);
            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n =>
                    n.NotificationId == id &&
                    (ids.Contains(n.UserId ?? string.Empty) ||
                     ids.Contains(n.TargetUserId ?? string.Empty)) &&
                    !senderIds.Contains(n.SenderUserId ?? string.Empty));

            if (notification != null)
            {
                _context.Notifications.Remove(notification);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Notifications", "Customer");
        }

        // Mark all customer notifications as read

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllNotificationsRead()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(code) &&
                !userId.HasValue)
            {
                return RedirectToAction("Login", "Home");
            }

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email.Trim().ToLower() == email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(code))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CustomerId == code);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            var ids = new List<string>();

            void AddCustomerId(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(value.Trim());
                }
            }

            AddCustomerId(customer.Email);
            AddCustomerId(customer.CustomerId);
            AddCustomerId(customer.Id.ToString());

            var senderIds = GetCustomerSenderIds(customer);
            // "Mark all as read": every unread notification becomes read in one go
            // (it moves from the Unread tab to read). Nothing is deleted.
            var unreadNotifications = await _context.Notifications
                .Where(n =>
                    !n.IsRead &&
                    (ids.Contains(n.UserId ?? string.Empty) ||
                     ids.Contains(n.TargetUserId ?? string.Empty)) &&
                    !senderIds.Contains(n.SenderUserId ?? string.Empty))
                .ToListAsync();

            foreach (var notification in unreadNotifications)
                notification.IsRead = true;

            if (unreadNotifications.Count > 0)
                await _context.SaveChangesAsync();

            return RedirectToAction("Notifications", "Customer");
        }

        // Clear read customer notifications

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearReadNotifications()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(code) &&
                !userId.HasValue)
            {
                return RedirectToAction("Login", "Home");
            }

            CustomerSignup? customer = null;

            if (!string.IsNullOrWhiteSpace(email))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c =>
                        c.Email != null &&
                        c.Email.Trim().ToLower() == email.Trim().ToLower());
            }

            if (customer == null && !string.IsNullOrWhiteSpace(code))
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CustomerId == code);
            }

            if (customer == null && userId.HasValue)
            {
                customer = await _context.CustomerSignup
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == userId.Value);
            }

            if (customer == null)
                return RedirectToAction("Login", "Home");

            var ids = new List<string>();

            void AddCustomerId(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ids.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(value.Trim());
                }
            }

            AddCustomerId(customer.Email);
            AddCustomerId(customer.CustomerId);
            AddCustomerId(customer.Id.ToString());

            var senderIds = GetCustomerSenderIds(customer);
            // "Clear" removes every notification of this customer (read and unread).
            var readNotifications = await _context.Notifications
                .Where(n =>
                    (ids.Contains(n.UserId ?? string.Empty) ||
                     ids.Contains(n.TargetUserId ?? string.Empty)) &&
                    !senderIds.Contains(n.SenderUserId ?? string.Empty))
                .ToListAsync();

            if (readNotifications.Count > 0)
            {
                _context.Notifications.RemoveRange(readNotifications);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Notifications", "Customer");
        }

        // Customer history
        // Builds a CustomerHistoryViewModel from the customer's bookings and
        // complaints and sends it to the History.cshtml view.

        [HttpGet]
        public async Task<IActionResult> History()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            var code = HttpContext.Session.GetString("UserCode");
            var userId = HttpContext.Session.GetInt32("UserId");

            if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(code) && !userId.HasValue)
                return RedirectToAction("Login", "Home");

            // Customer
            CustomerSignup? customer = null;
            if (!string.IsNullOrWhiteSpace(email))
                customer = await _context.CustomerSignup.AsNoTracking().FirstOrDefaultAsync(c => c.Email != null && c.Email.Trim().ToLower() == email.Trim().ToLower());
            if (customer == null && !string.IsNullOrWhiteSpace(code))
                customer = await _context.CustomerSignup.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == code);
            if (customer == null && userId.HasValue)
                customer = await _context.CustomerSignup.AsNoTracking().FirstOrDefaultAsync(c => c.Id == userId.Value);
            if (customer == null)
                return RedirectToAction("Login", "Home");

            email = customer.Email ?? email;

            ViewBag.CustomerProfileImage = customer.ProfileImage;
            ViewBag.UnreadNotificationCount = await GetCustomerUnreadNotificationCountAsync(customer);

            // All customer bookings relevant to history
            //
            // Job History shows ONLY Completed, Cancelled and Rejected bookings.
            // Pending / Accepted / Expired bookings are not part of History.
            var bookings = await _context.Booking
                .AsNoTracking()
                .Where(b =>
                    b.CustomerID == (HttpContext.Session.GetString("UserCode") ?? $"CUS-{HttpContext.Session.GetInt32("UserId")}") &&
                    (
                        b.Status != null &&
                        (b.Status.Trim().ToLower() == "completed" ||
                         b.Status.Trim().ToLower() == "cancelled" ||
                         b.Status.Trim().ToLower() == "rejected")
                    ))
                .OrderByDescending(b => b.Id)
                .ToListAsync();

            // Provider names
            var providerIds = bookings
                .Where(b => b.ProviderId > 0)
                .Select(b => b.ProviderId)
                .Distinct()
                .ToList();

            var providerNames = new Dictionary<int, string>();

            if (providerIds.Count > 0)
            {
                var providers = await _context.Register
                    .AsNoTracking()
                    .Where(r => providerIds.Contains(r.Id))
                    .Select(r => new
                    {
                        r.Id,
                        r.Name
                    })
                    .ToListAsync();

                providerNames = providers.ToDictionary(
                    x => x.Id,
                    x => x.Name ?? "Not Assigned");
            }

            // Job history
            var jobHistory = bookings
                .Select(b =>
                {
                    string providerName = !string.IsNullOrWhiteSpace(b.ProviderName)
                        ? b.ProviderName
                        : (
                            b.ProviderId > 0 &&
                            providerNames.TryGetValue(b.ProviderId, out var name)
                                ? name
                                : "Not Assigned"
                        );

                    bool hasRescheduleHistory =
                        !string.IsNullOrWhiteSpace(b.RescheduleRequestedBy) ||
                        b.RequestedDate.HasValue ||
                        !string.IsNullOrWhiteSpace(b.RequestedTime) ||
                        b.RescheduledDate.HasValue ||
                        !string.IsNullOrWhiteSpace(b.RescheduledTime) ||
                        !string.IsNullOrWhiteSpace(b.RescheduledStatus) ||
                        b.IsRescheduledByCustomer ||
                        b.IsRescheduledByProvider;

                    return new CustomerJobHistoryItem
                    {
                        Id = b.Id,

                        BookingId = string.IsNullOrWhiteSpace(b.BookingId)
                            ? b.Id.ToString()
                            : b.BookingId,

                        CustomerName = b.CustomerName ?? customer.Name ?? "Customer",

                        ProviderName = providerName,

                        ServiceName = b.ServiceName ?? string.Empty,

                        ServiceType = b.ServiceType ?? string.Empty,

                        Address = b.Address ?? string.Empty,

                        ProblemDetails = b.ProblemDetails ?? string.Empty,

                        BookingDate = b.BookingDate,

                        BookingTime = b.BookingTime ?? string.Empty,

                        Status = string.IsNullOrWhiteSpace(b.Status)
                            ? "Unknown"
                            : b.Status,

                        CompletedAt = b.CompletedAt,

                        CancelledAt = b.CancelledAt,

                        CancelledBy = b.CancelledBy,

                        RejectedAt = b.RejectedAt,

                        RescheduleRequestedBy = b.RescheduleRequestedBy,

                        RequestedDate = b.RequestedDate,

                        RequestedTime = b.RequestedTime,

                        RescheduledDate = b.RescheduledDate,

                        RescheduledTime = b.RescheduledTime,

                        RescheduledStatus = b.RescheduledStatus,

                        HasRescheduleHistory = hasRescheduleHistory,

                        VisitCharges = b.VisitCharges
                    };
                })
                .ToList();

            // Rating history
            //
            // Customer rating is stored in Booking.Rating and Booking.ReviewComment.
            var ratingHistory = bookings
                .Where(b => b.Rating.HasValue)
                .Select(b =>
                {
                    string providerName = !string.IsNullOrWhiteSpace(b.ProviderName)
                        ? b.ProviderName
                        : (
                            b.ProviderId > 0 &&
                            providerNames.TryGetValue(b.ProviderId, out var name)
                                ? name
                                : "Not Assigned"
                        );

                    return new CustomerRatingHistoryItem
                    {
                        BookingDatabaseId = b.Id,

                        BookingId = string.IsNullOrWhiteSpace(b.BookingId)
                            ? b.Id.ToString()
                            : b.BookingId,

                        ProviderName = providerName,

                        ServiceName = b.ServiceName ?? string.Empty,

                        ServiceType = b.ServiceType ?? string.Empty,

                        BookingDate = b.BookingDate,

                        Rating = b.Rating!.Value,

                        ReviewComment = b.ReviewComment ?? string.Empty
                    };
                })
                .OrderByDescending(x => x.BookingDate)
                .ToList();

            // Complaint history
            //
            // ComplaintModel has CustomerEmail, so complaints are filtered
            // directly by the logged-in customer's email.
            var complaintHistory = await _context.Complaints
                .AsNoTracking()
                .Where(c => c.CustomerEmail != null &&
                            c.CustomerEmail.Trim().ToLower() == email.Trim().ToLower())
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            // Final strongly-typed view model
            var model = new CustomerHistoryViewModel
            {
                JobHistory = jobHistory,
                RatingHistory = ratingHistory,
                ComplaintHistory = complaintHistory
            };

            return View(model);
        }

        // Logout

        // GET only opens the logout confirmation page.
        // It does not clear the session.
        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            var email = HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToAction("Index", "Home");
            }

            // Fetch only the profile-image column needed by the logout page.
            // This keeps the existing logout flow unchanged while allowing
            // the mobile profile image to display correctly.
            var profileImage = await _context.CustomerSignup
                .AsNoTracking()
                .Where(c => c.Email == email)
                .Select(c => c.ProfileImage)
                .FirstOrDefaultAsync();

            ViewBag.CustomerProfileImage = profileImage;

            return View();
        }

        // Yes logout
        // Clear the customer session and then go to Home/Index.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult LogoutConfirmed()
        {
            HttpContext.Session.Clear();

            return RedirectToAction("Index", "Home");
        }

    }
}