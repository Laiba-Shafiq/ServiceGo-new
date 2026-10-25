using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceGo.Data;
using ServiceGo.Models;

namespace ServiceGo.Controllers
{
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public AdminController(
        AppDbContext context,
        IWebHostEnvironment webHostEnvironment)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
        }

        // Provider notification helper

        private void AddProviderNotification(
        Register provider,
        string title,
        string message,
        string notificationType,
        string iconClass,
        string? referenceId = null,
        string? actionUrl = null)
        {
            if (provider == null)
                return;

            // Role specific code (PRO-1), so a provider is never confused with a
            // customer who has the same database number.
            string providerId = !string.IsNullOrWhiteSpace(provider.ProviderId)
                ? provider.ProviderId.Trim()
                : "PRO-" + provider.Id;

            _context.Notifications.Add(new Notification
            {
                UserId = providerId,
                TargetUserId = providerId,
                SenderUserId = "Admin",
                Title = title,
                Message = message,
                NotificationType = notificationType,
                IconClass = iconClass,
                IsRead = false,
                Timestamp = DateTime.Now,
                ReferenceId = referenceId,
                ActionUrl = actionUrl
            });
        }

        private void AddCustomerNotification(
        CustomerSignup customer,
        string title,
        string message,
        string notificationType,
        string iconClass,
        string? referenceId = null,
        string? actionUrl = null)
        {
            if (customer == null)
                return;

            // Role specific code (CUS-1), so a customer is never confused with a
            // provider who has the same database number.
            string customerId = !string.IsNullOrWhiteSpace(customer.CustomerId)
                ? customer.CustomerId.Trim()
                : "CUS-" + customer.Id;

            _context.Notifications.Add(new Notification
            {
                UserId = customerId,
                TargetUserId = customerId,
                SenderUserId = "Admin",
                Title = title,
                Message = message,
                NotificationType = notificationType,
                IconClass = iconClass,
                IsRead = false,
                Timestamp = DateTime.Now,
                ReferenceId = referenceId,
                ActionUrl = actionUrl
            });
        }

        // Admin dashboard

        [HttpGet]
        public IActionResult Dashboard()
        {
            var model = new AdminDashboard
            {
                TotalCustomers = _context.CustomerSignup.AsNoTracking().Count(),

                TotalProviders = _context.Register.AsNoTracking().Count(
            r => r.Role == "Provider" &&
            r.IsApproved == true
            ),

                PendingComplaints = _context.Complaints.AsNoTracking().Count(
            c => c.Status == "Pending"
            )
            };

            return View(model);
        }

        // Pending providers
        public async Task<IActionResult> PendingProviders()
        {
            var pending = await _context.Register
            .AsNoTracking()
            .Where(p =>
            p.Role == "Provider" &&
            p.IsApproved == false &&
            p.IsRejected == false &&
            p.IsBlocked == false)
            .ToListAsync();

            return View(pending);
        }

        // Active providers

        public async Task<IActionResult> ActiveProviders()
        {
            var active = await _context.Register
            .AsNoTracking()
            .Where(p =>
            p.Role == "Provider" &&
            p.IsApproved == true &&
            p.IsRejected == false &&
            p.IsBlocked == false)
            .ToListAsync();

            return View(active);
        }

        // Approve provider

        [HttpPost]
        public async Task<IActionResult> ApproveProvider(string id)
        {
            var provider = await FindProviderByAnyIdAsync(id);

            if (provider == null)
            {
                return Json(new { success = false, message = "Provider not found." });
            }

            provider.IsApproved = true;
            provider.IsRejected = false;
            provider.IsBlocked = false;
            provider.RejectionReason = null;

            // Keep ProviderSignup.Status in sync (shows "Active" in the database).
            await SyncProviderSignupStatusAsync(provider, "Active");

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Provider approved successfully."
            });
        }

        // Rejected providers

        public async Task<IActionResult> RejectedProviders()
        {
            var list = await _context.Register
            .AsNoTracking()
            .Where(p =>
            p.Role == "Provider" &&
            p.IsRejected == true &&
            p.IsApproved == false &&
            p.IsBlocked == false)
            .ToListAsync();

            return View(list);
        }

        // Reject provider

        [HttpPost]
        public async Task<IActionResult> RejectProvider(string id, string reason)
        {
            var provider = await FindProviderByAnyIdAsync(id);

            if (provider == null)
            {
                return Json(new { success = false, message = "Provider not found." });
            }

            reason = (reason ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(reason))
            {
                return Json(new { success = false, message = "Rejection reason is required." });
            }

            provider.IsApproved = false;
            provider.IsRejected = true;
            provider.IsBlocked = false;
            provider.RejectionReason = reason;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Provider rejected successfully."
            });
        }

        // Blocked providers

        public async Task<IActionResult> BlockedProviders()
        {
            var blockedList = await _context.Register
            .AsNoTracking()
            .Where(p =>
            p.Role == "Provider" &&
            p.IsBlocked == true)
            .ToListAsync();

            return View(blockedList);
        }

        // Block provider

        [HttpPost]
        public async Task<IActionResult> BlockProvider(string id)
        {
            var provider = await FindProviderByAnyIdAsync(id);

            if (provider == null)
            {
                return Json(new { success = false, message = "Provider not found." });
            }

            provider.IsApproved = false;
            provider.IsRejected = false;
            provider.IsBlocked = true;
            provider.RejectionReason = null;

            await SyncProviderSignupStatusAsync(provider, "Blocked");

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Provider blocked successfully."
            });
        }

        // Unblock provider

        [HttpPost]
        public async Task<IActionResult> UnblockProvider(string id)
        {
            var provider = await FindProviderByAnyIdAsync(id);

            if (provider == null)
            {
                return Json(new { success = false, message = "Provider not found." });
            }

            provider.IsApproved = true;
            provider.IsRejected = false;
            provider.IsBlocked = false;
            provider.RejectionReason = null;

            await SyncProviderSignupStatusAsync(provider, "Active");

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Provider unblocked successfully."
            });
        }

        private static string GetCustomerJoinDate(CustomerSignup customer)
        {
            if (customer == null) return "N/A";

            var type = customer.GetType();
            string[] propertyNames =
            {
"CreatedAt", "CreatedDate", "RegisteredAt", "RegistrationDate",
"JoinDate", "JoinedDate", "DateCreated", "DateRegistered"
};

            foreach (var name in propertyNames)
            {
                var property = type.GetProperty(name);
                if (property == null) continue;

                var value = property.GetValue(customer);
                if (value is DateTime dt && dt != default)
                    return dt.ToString("dd MMM yyyy");

                if (value is DateTimeOffset dto && dto != default)
                    return dto.ToString("dd MMM yyyy");
            }

            return "N/A";
        }

        // Customer management

        // Builds a lookup that can be found by BOTH the database Id ("1")
        // and the stored customer code ("CUS-1"), so City / Profile Image /
        // Latitude / Longitude are always found for the right customer.
        private static Dictionary<string, TValue> BuildCustomerLookup<TValue>(
        IEnumerable<CustomerSignup> customers,
        Func<CustomerSignup, TValue> valueSelector)
        {
            var lookup = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in customers)
            {
                var value = valueSelector(c);

                lookup[c.Id.ToString()] = value;
                lookup["CUS-" + c.Id] = value;

                if (!string.IsNullOrWhiteSpace(c.CustomerId))
                    lookup[c.CustomerId.Trim()] = value;
            }

            return lookup;
        }

        private static string GetCustomerCode(CustomerSignup c)
        {
            return "CUS-" + c.Id;
        }

        public IActionResult ActiveCustomers()
        {
            var customers = _context.CustomerSignup
            .AsNoTracking()
            .Where(c => c.Status == "Active" || c.Status == null)
            .ToList();

            var activeCustomers = customers
            .Select(c => new ActiveCustomerViewModel
            {
                CustomerId = c.Id,
                Name = c.Name ?? "N/A",
                Email = c.Email ?? "N/A",
                Phone = c.Phone ?? "N/A",
                Address = string.IsNullOrWhiteSpace(c.Address) ? "Not Provided" : c.Address,
                JoinedDate = GetCustomerJoinDate(c),
                Status = c.Status ?? "Active"
            })
            .ToList();

            // Keep the extra customer details available to the existing
            // details popup without requiring these fields in
            // ActiveCustomerViewModel.
            ViewBag.CustomerCodes = BuildCustomerLookup(customers, GetCustomerCode);

            ViewBag.CustomerCities = BuildCustomerLookup(
            customers,
            c => string.IsNullOrWhiteSpace(c.City) ? "N/A" : c.City.Trim());

            ViewBag.CustomerProfileImages = BuildCustomerLookup(
            customers,
            c => c.ProfileImage ?? string.Empty);

            ViewBag.CustomerLatitudes = BuildCustomerLookup(customers, c => c.Latitude);

            ViewBag.CustomerLongitudes = BuildCustomerLookup(customers, c => c.Longitude);

            return View(activeCustomers);
        }

        // Block customer

        [HttpPost]
        public async Task<IActionResult> BlockCustomer(string id)
        {
            var customer = await FindCustomerByAnyIdAsync(id);

            if (customer == null)
                return RedirectToAction("ActiveCustomers");

            customer.Status = "Blocked";

            await _context.SaveChangesAsync();

            return RedirectToAction("BlockedCustomers");
        }

        // Unblock customer

        [HttpPost]
        public async Task<IActionResult> UnblockCustomer(string id)
        {
            var customer = await FindCustomerByAnyIdAsync(id);

            if (customer != null)
            {
                customer.Status = "Active";
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("BlockedCustomers");
        }

        // Blocked customers

        public IActionResult BlockedCustomers()
        {
            var blockedCustomers = _context.CustomerSignup
            .AsNoTracking()
            .Where(c => c.Status != null && c.Status.ToLower() == "blocked")
            .ToList();

            var blockedList = blockedCustomers
            .Select(c => new ActiveCustomerViewModel
            {
                CustomerId = c.Id,
                Name = c.Name ?? "N/A",
                Email = c.Email ?? "N/A",
                Phone = c.Phone ?? "N/A",
                Address = string.IsNullOrWhiteSpace(c.Address) ? "Not Provided" : c.Address,
                JoinedDate = GetCustomerJoinDate(c),
                Status = "Blocked"
            })
            .ToList();

            // Keep the same customer details available if the blocked-customer
            // view uses the shared customer details popup.
            ViewBag.CustomerCodes = BuildCustomerLookup(blockedCustomers, GetCustomerCode);

            ViewBag.CustomerCities = BuildCustomerLookup(
            blockedCustomers,
            c => string.IsNullOrWhiteSpace(c.City) ? "N/A" : c.City.Trim());

            ViewBag.CustomerProfileImages = BuildCustomerLookup(
            blockedCustomers,
            c => c.ProfileImage ?? string.Empty);

            ViewBag.CustomerLatitudes = BuildCustomerLookup(blockedCustomers, c => c.Latitude);

            ViewBag.CustomerLongitudes = BuildCustomerLookup(blockedCustomers, c => c.Longitude);

            return View(blockedList);
        }

        // Complaint management

        [HttpGet]
        public async Task<IActionResult> Complaint(string? view = null)
        {
            // Complaint page contains only complaints that still need an admin action.
            // Warned complaints move to Send Warning; blocked complaints move to
            // the appropriate Blocked Customers/Providers page.
            var allComplaints = await _context.Complaints
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

            // Fill runtime target information for the View and history calculations.
            // These properties are [NotMapped], so NO migration is required.
            var complaintsList = allComplaints;

            foreach (var complaint in complaintsList)
            {
                await PopulateComplaintTargetAsync(complaint);

                // Keep the Admin page truthful even for older records where
                // the form stored the selected issue in OtherIssue.
                complaint.IssueCategory = GetComplaintIssue(complaint);
            }

            // Calculate history against the same target user using ALL complaint history.
            foreach (var complaint in complaintsList)
            {
                string targetKey = GetComplaintTargetKey(complaint);

                if (string.IsNullOrWhiteSpace(targetKey))
                    continue;

                var previous = complaintsList
                .Where(c =>
                c.Id != complaint.Id &&
                GetComplaintTargetKey(c) == targetKey)
                .ToList();

                complaint.PreviousComplaintsCount = previous.Count;

                complaint.PreviousWarningsCount = previous.Count(c =>
                c.WarningSent ||
                string.Equals(c.Status, "Warning Sent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Status, "WarningSent", StringComparison.OrdinalIgnoreCase));

                complaint.PreviousBlockedCount = previous.Count(c =>
                string.Equals(c.Status, "Blocked", StringComparison.OrdinalIgnoreCase));
            }

            complaintsList = complaintsList
            .Where(c =>
            !c.WarningSent &&
            !string.Equals(c.Status, "Warning Sent", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(c.Status, "WarningSent", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(c.Status, "Blocked", StringComparison.OrdinalIgnoreCase))
            .ToList();

            // Warning records are shown in the Warnings Sent tab on the same Complaint page.
            // Filtering is done in memory so EF Core does not have to translate StringComparison.
            var warningsList = allComplaints
            .Where(c => c.WarningSent)
            .Where(c => !string.Equals(c.Status, "Blocked", StringComparison.OrdinalIgnoreCase))
            .ToList();

            foreach (var warning in warningsList)
            {
                await PopulateComplaintTargetAsync(warning);
                warning.IssueCategory = GetComplaintIssue(warning);
            }

            ViewBag.WarningComplaints = warningsList;

            return View(complaintsList);
        }

        // Sent warnings

        [HttpGet]
        public async Task<IActionResult> SendWarning()
        {
            var warnings = await _context.Complaints
            .Where(c =>
            c.WarningSent &&
            (c.Status == null ||
            (c.Status != "Blocked" &&
            c.Status != "blocked" &&
            c.Status != "BLOCKED")))
            .OrderByDescending(c => c.Id)
            .ToListAsync();

            foreach (var complaint in warnings)
            {
                await PopulateComplaintTargetAsync(complaint);
            }

            return View(warnings);
        }

        // Complaint target helpers

        private static string NormalizeUserId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return string.Empty;

            var value = id.Trim();

            if (value.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(4);

            if (value.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(4);

            return value.Trim();
        }

        private async Task<Register?> FindProviderByAnyIdAsync(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string raw = id.Trim();

            // Accept the ID formats already used across the Admin pages
            // PRO-123, PRV-123, 123, and the stored ProviderId itself.
            var candidates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    candidates.Add(value.Trim());
            }

            AddCandidate(raw);

            string normalized = raw;

            while (
            normalized.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("PRV-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(4).Trim();
            }

            AddCandidate(normalized);
            AddCandidate("PRO-" + normalized);
            AddCandidate("PRV-" + normalized);

            var provider = await _context.Register
            .FirstOrDefaultAsync(p =>
            p.Role == "Provider" &&
            p.ProviderId != null &&
            candidates.Contains(p.ProviderId.Trim()));

            if (provider != null)
                return provider;

            if (int.TryParse(normalized, out int dbId))
            {
                return await _context.Register.FirstOrDefaultAsync(p =>
                p.Role == "Provider" &&
                p.Id == dbId);
            }

            return null;
        }

        // Keeps ProviderSignup.Status in sync with the provider's Register flags.
        // The change is saved by the caller's SaveChangesAsync.
        private async Task SyncProviderSignupStatusAsync(Register provider, string status)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Email))
                return;

            string email = provider.Email.Trim().ToLower();

            var signup = await _context.ProviderSignup
            .FirstOrDefaultAsync(p =>
            p.Email != null &&
            p.Email.Trim().ToLower() == email);

            if (signup != null)
                signup.Status = status;
        }

        private async Task<CustomerSignup?> FindCustomerByAnyIdAsync(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string raw = id.Trim();
            string clean = raw;

            while (clean.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(4).Trim();

            var candidates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

            candidates.Add(raw);
            candidates.Add(clean);
            candidates.Add("CUS-" + clean);

            var customer = await _context.CustomerSignup
            .FirstOrDefaultAsync(c =>
            c.CustomerId != null &&
            candidates.Contains(c.CustomerId.Trim()));

            if (customer != null)
                return customer;

            if (int.TryParse(clean, out int dbId))
            {
                return await _context.CustomerSignup.FirstOrDefaultAsync(c =>
                c.Id == dbId);
            }

            return null;
        }

        private async Task PopulateComplaintTargetAsync(ComplaintModel complaint)
        {
            complaint.TargetRole = string.Empty;
            complaint.TargetUserId = string.Empty;
            complaint.TargetUserName = string.Empty;
            complaint.TargetPhone = string.Empty;
            complaint.TargetEmail = string.Empty;
            complaint.TargetIsBlocked = false;

            if (string.Equals(
            complaint.ComplaintType,
            "Customer vs Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                var provider = await FindProviderByAnyIdAsync(complaint.ProviderId);

                if (provider != null)
                {
                    complaint.TargetRole = "Provider";
                    complaint.TargetUserId =
                    !string.IsNullOrWhiteSpace(provider.ProviderId)
                    ? provider.ProviderId
                    : "PRO-" + provider.Id;

                    complaint.TargetUserName = provider.Name ?? "Provider";
                    complaint.TargetPhone = provider.PhoneNumber ?? string.Empty;
                    complaint.TargetEmail = provider.Email ?? string.Empty;
                    complaint.TargetIsBlocked =
                    string.Equals(provider.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ||
                    (!provider.IsApproved && provider.IsRejected);
                }
                else
                {
                    complaint.TargetRole = "Provider";
                    complaint.TargetUserId = complaint.ProviderId ?? string.Empty;
                    complaint.TargetUserName = complaint.ProviderName ?? "Provider";
                }
            }
            else if (string.Equals(
            complaint.ComplaintType,
            "Provider vs Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                var customer = await FindCustomerByAnyIdAsync(complaint.CustomerId);

                if (customer != null)
                {
                    complaint.TargetRole = "Customer";
                    complaint.TargetUserId =
                    !string.IsNullOrWhiteSpace(customer.CustomerId)
                    ? customer.CustomerId
                    : "CUS-" + customer.Id;

                    complaint.TargetUserName = customer.Name ?? "Customer";
                    complaint.TargetPhone = string.Empty;
                    complaint.TargetEmail = customer.Email ?? string.Empty;
                    complaint.TargetIsBlocked =
                    string.Equals(customer.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    complaint.TargetRole = "Customer";
                    complaint.TargetUserId = complaint.CustomerId ?? string.Empty;
                    complaint.TargetUserName = complaint.CustomerName ?? "Customer";
                }
            }
        }

        private string GetComplaintTargetKey(ComplaintModel complaint)
        {
            if (complaint == null)
                return string.Empty;

            if (string.Equals(
            complaint.TargetRole,
            "Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                return "PROVIDER:" + NormalizeUserId(complaint.TargetUserId);
            }

            if (string.Equals(
            complaint.TargetRole,
            "Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                return "CUSTOMER:" + NormalizeUserId(complaint.TargetUserId);
            }

            if (string.Equals(
            complaint.ComplaintType,
            "Customer vs Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                return "PROVIDER:" + NormalizeUserId(complaint.ProviderId);
            }

            if (string.Equals(
            complaint.ComplaintType,
            "Provider vs Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                return "CUSTOMER:" + NormalizeUserId(complaint.CustomerId);
            }

            return string.Empty;
        }

        // Complaint issue - use the exact issue from the form
        private static string GetComplaintIssue(ComplaintModel complaint)
        {
            if (complaint == null)
                return "Reported Issue";

            // The complaint form may store the selected issue in OtherIssue.
            // Prefer that exact value whenever it is a real selected/custom issue.
            var otherIssue = complaint.OtherIssue?.Trim();

            if (!string.IsNullOrWhiteSpace(otherIssue) &&
            !string.Equals(otherIssue, "Other", StringComparison.OrdinalIgnoreCase))
            {
                return otherIssue;
            }

            var category = complaint.IssueCategory?.Trim();

            if (!string.IsNullOrWhiteSpace(category) &&
            !string.Equals(category, "General", StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }

            if (string.Equals(otherIssue, "Other", StringComparison.OrdinalIgnoreCase))
                return "Other";

            return "Reported Issue";
        }

        // Complaint block reason

        private static string BuildComplaintBlockReason(ComplaintModel complaint)
        {
            if (complaint == null)
                return "Your account has been blocked by the administrator.";

            string issue = string.IsNullOrWhiteSpace(complaint.IssueCategory)
            ? "the reported complaint"
            : complaint.IssueCategory.Trim();

            if (!string.IsNullOrWhiteSpace(complaint.ComplaintDetails))
            {
                return $"Your account has been blocked by the administrator following review of the complaint regarding {issue}. Reason: {complaint.ComplaintDetails.Trim()}";
            }

            return $"Your account has been blocked by the administrator following review of the complaint regarding {issue}.";
        }

        // Find complaint flexibly

        private async Task<ComplaintModel?> FindComplaintAsync(
        string? complaintIdInput,
        int? primaryId)
        {
            if (primaryId.HasValue && primaryId.Value > 0)
            {
                var byId = await _context.Complaints
                .FirstOrDefaultAsync(c => c.Id == primaryId.Value);

                if (byId != null)
                    return byId;
            }

            if (string.IsNullOrWhiteSpace(complaintIdInput))
                return null;

            string cleanId = complaintIdInput
            .Replace("#", "")
            .Trim();

            var complaint = await _context.Complaints
            .FirstOrDefaultAsync(c =>
            c.ComplaintId == cleanId ||
            c.ComplaintId == complaintIdInput.Trim());

            if (complaint != null)
                return complaint;

            if (int.TryParse(cleanId, out int parsedId))
            {
                return await _context.Complaints
                .FirstOrDefaultAsync(c => c.Id == parsedId);
            }

            return null;
        }

        // Mark complaint as resolved

        [HttpPost]
        public async Task<IActionResult> MarkComplaintResolved(
        [FromBody] ComplaintActionRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "Invalid complaint request." });

            var complaint = await FindComplaintAsync(
            request.ComplaintId,
            request.Id);

            if (complaint == null)
                return NotFound(new { success = false, message = "Complaint not found." });

            complaint.Status = "Resolved";

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Complaint marked as resolved successfully.",
                complaintId = complaint.ComplaintId,
                id = complaint.Id,
                status = complaint.Status,
                nextTab = "Resolved"
            });
        }

        // Send warning

        [HttpPost]
        public async Task<IActionResult> SendWarning(
        [FromBody] ComplaintWarningRequest request)
        {
            if (request == null)
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid complaint request."
                });

            var complaint = await FindComplaintAsync(
            request.ComplaintId,
            request.Id);

            if (complaint == null)
                return NotFound(new
                {
                    success = false,
                    message = "Complaint not found."
                });

            await PopulateComplaintTargetAsync(complaint);

            if (complaint.WarningSent)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "A warning has already been sent for this complaint."
                });
            }

            string issue = GetComplaintIssue(complaint);
            complaint.IssueCategory = issue;

            // Important
            // There are TWO different people in every complaint
            // 1) Reported user -> receives the WARNING.
            // 2) Complainant -> receives the COMPLAINT UPDATE.
            // Exactly ONE notification is created for each person.
            // They must NEVER receive the same message.

            Register? reportedProvider = null;
            CustomerSignup? reportedCustomer = null;
            CustomerSignup? customerComplainant = null;
            Register? providerComplainant = null;

            if (string.Equals(
            complaint.ComplaintType,
            "Customer vs Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                // Customer complained about Provider.
                reportedProvider =
                await FindProviderByAnyIdAsync(complaint.ProviderId);

                customerComplainant =
                await FindCustomerByAnyIdAsync(complaint.CustomerId);
            }
            else if (string.Equals(
            complaint.ComplaintType,
            "Provider vs Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                // Provider complained about Customer.
                reportedCustomer =
                await FindCustomerByAnyIdAsync(complaint.CustomerId);

                providerComplainant =
                await FindProviderByAnyIdAsync(complaint.ProviderId);
            }

            // Remove only the old complaint notification(s) for this
            // Complaint and these two participants.
            // Complaint submission originally creates a "Complaint"
            // notification for the reported user. Once Admin presses
            // Send Warning, that old notification is replaced by the
            // final warning/update pair so the user does not see
            // duplicate complaint messages.

            var complaintRef = complaint.ComplaintId?.Trim();

            var participantIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

            void AddParticipantIds(
            string? numericId,
            string? email,
            string? roleCode)
            {
                foreach (var value in new[]
                {
numericId,
email,
roleCode
})
                {
                    if (!string.IsNullOrWhiteSpace(value))
                        participantIds.Add(value.Trim());
                }
            }

            if (reportedProvider != null)
            {
                AddParticipantIds(
                reportedProvider.Id.ToString(),
                reportedProvider.Email,
                reportedProvider.ProviderId);
            }

            if (reportedCustomer != null)
            {
                AddParticipantIds(
                reportedCustomer.Id.ToString(),
                reportedCustomer.Email,
                reportedCustomer.CustomerId);
            }

            if (customerComplainant != null)
            {
                AddParticipantIds(
                customerComplainant.Id.ToString(),
                customerComplainant.Email,
                customerComplainant.CustomerId);
            }

            if (providerComplainant != null)
            {
                AddParticipantIds(
                providerComplainant.Id.ToString(),
                providerComplainant.Email,
                providerComplainant.ProviderId);
            }

            if (!string.IsNullOrWhiteSpace(complaintRef) &&
            participantIds.Count > 0)
            {
                var oldComplaintNotifications =
                await _context.Notifications
                .Where(n =>
                n.ReferenceId == complaintRef &&
                n.UserId != null && participantIds.Contains(n.UserId) &&
                n.NotificationType == "Complaint")
                .ToListAsync();

                if (oldComplaintNotifications.Count > 0)
                    _context.Notifications.RemoveRange(
                    oldComplaintNotifications);
            }

            complaint.WarningSent = true;
            complaint.Status = "Warning Sent";

            // Helper for this admin action only
            // Use role-specific IDs (PRO-xxx / CUS-xxx) for the new
            // notifications. This prevents a provider DB Id and a
            // customer DB Id with the same number from being confused.

            void AddAdminProviderNotification(
            Register provider,
            string title,
            string message,
            string notificationType,
            string iconClass,
            string? referenceId,
            string? actionUrl)
            {
                if (provider == null)
                    return;

                string targetId =
                !string.IsNullOrWhiteSpace(provider.ProviderId)
                ? provider.ProviderId.Trim()
                : "PRO-" + provider.Id;

                _context.Notifications.Add(new Notification
                {
                    UserId = targetId,
                    TargetUserId = targetId,
                    SenderUserId = "Admin",
                    Title = title,
                    Message = message,
                    NotificationType = notificationType,
                    IconClass = iconClass,
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    ReferenceId = referenceId,
                    ActionUrl = actionUrl
                });
            }

            void AddAdminCustomerNotification(
            CustomerSignup customer,
            string title,
            string message,
            string notificationType,
            string iconClass,
            string? referenceId,
            string? actionUrl)
            {
                if (customer == null)
                    return;

                string targetId =
                !string.IsNullOrWhiteSpace(customer.CustomerId)
                ? customer.CustomerId.Trim()
                : "CUS-" + customer.Id;

                _context.Notifications.Add(new Notification
                {
                    UserId = targetId,
                    TargetUserId = targetId,
                    SenderUserId = "Admin",
                    Title = title,
                    Message = message,
                    NotificationType = notificationType,
                    IconClass = iconClass,
                    IsRead = false,
                    Timestamp = DateTime.Now,
                    ReferenceId = referenceId,
                    ActionUrl = actionUrl
                });
            }

            // 1) Exactly one warning -> reported user

            if (reportedProvider != null)
            {
                string complainantName =
                !string.IsNullOrWhiteSpace(customerComplainant?.Name)
                ? customerComplainant.Name.Trim()
                : "a customer";

                AddAdminProviderNotification(
                reportedProvider,
                "Official ServiceGo Warning",
                $"A complaint (ID: {complaint.ComplaintId}) has been filed against your account by {complainantName} regarding {issue}. Please make sure this does not happen again. If we receive another complaint, your ServiceGo account may be blocked.",
                "Warning",
                "fa-solid fa-triangle-exclamation",
                complaint.ComplaintId,
                Url.Action("Notifications", "Provider"));
            }
            else if (reportedCustomer != null)
            {
                string complainantName =
                !string.IsNullOrWhiteSpace(providerComplainant?.Name)
                ? providerComplainant.Name.Trim()
                : "a provider";

                AddAdminCustomerNotification(
                reportedCustomer,
                "Official ServiceGo Warning",
                $"A complaint (ID: {complaint.ComplaintId}) has been filed against your account by {complainantName} regarding {issue}. Please make sure this does not happen again. If we receive another complaint, your ServiceGo account may be blocked.",
                "Warning",
                "fa-solid fa-triangle-exclamation",
                complaint.ComplaintId,
                Url.Action("Notifications", "Customer"));
            }

            // 2) Exactly one complaint update -> complainant

            if (customerComplainant != null)
            {
                AddAdminCustomerNotification(
                customerComplainant,
                "Complaint Update",
                $"Your complaint (ID: {complaint.ComplaintId}) has been received and reviewed by our support team. We have sent an official warning to the reported user and are taking action on the issue. InshaAllah, your complaint will be resolved soon.",
                "Complaint",
                "fa-solid fa-shield-check",
                complaint.ComplaintId,
                Url.Action("Notifications", "Customer"));
            }
            else if (providerComplainant != null)
            {
                AddAdminProviderNotification(
                providerComplainant,
                "Complaint Update",
                $"Your complaint (ID: {complaint.ComplaintId}) has been received and reviewed by our support team. We have sent an official warning to the reported user and are taking action on the issue. InshaAllah, your complaint will be resolved soon.",
                "Complaint",
                "fa-solid fa-shield-check",
                complaint.ComplaintId,
                Url.Action("Notifications", "Provider"));
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message =
            "Warning sent successfully. One warning was sent to the reported user and one complaint update was sent to the complainant.",
                complaintId = complaint.ComplaintId,
                id = complaint.Id,
                status = complaint.Status,
                nextPage =
            Url.Action(
            "Complaint",
            "Admin",
            new { view = "warnings" })
            });
        }

        [HttpPost]
        public async Task<IActionResult> BlockComplaintUser(
        [FromBody] ComplaintBlockRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "Invalid complaint request." });

            var complaint = await FindComplaintAsync(
            request.ComplaintId,
            request.Id);

            if (complaint == null)
                return NotFound(new { success = false, message = "Complaint not found." });

            await PopulateComplaintTargetAsync(complaint);

            bool userBlocked = false;
            string blockedUserType = string.Empty;

            if (string.Equals(
            complaint.TargetRole,
            "Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                var provider =
                await FindProviderByAnyIdAsync(complaint.TargetUserId);

                if (provider != null)
                {
                    provider.IsApproved = false;
                    provider.IsRejected = true;
                    provider.IsBlocked = true;

                    await SyncProviderSignupStatusAsync(provider, "Blocked");

                    complaint.Status = "Blocked";

                    // IMPORTANT: Do not notify the blocked provider.
                    // The blocked popup is shown on the next login attempt.
                    userBlocked = true;
                    blockedUserType = "Provider";
                }
            }
            else if (string.Equals(
            complaint.TargetRole,
            "Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                var customer =
                await FindCustomerByAnyIdAsync(complaint.TargetUserId);

                if (customer != null)
                {
                    customer.Status = "Blocked";
                    complaint.Status = "Blocked";

                    // IMPORTANT: Do not notify the blocked customer.
                    // The blocked popup is shown on the next login attempt.
                    userBlocked = true;
                    blockedUserType = "Customer";
                }
            }

            if (!userBlocked)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Target user could not be found."
                });
            }

            complaint.Status = "Blocked";

            if (string.Equals(
            complaint.ComplaintType,
            "Customer vs Provider",
            StringComparison.OrdinalIgnoreCase))
            {
                var complainant =
                await FindCustomerByAnyIdAsync(complaint.CustomerId);

                if (complainant != null)
                {
                    AddCustomerNotification(
                    complainant,
                    "Complaint Resolved",
                    $"Thank you for reporting this issue. After reviewing your complaint (ID: {complaint.ComplaintId}), we have blocked the reported user's account. Your complaint is now resolved. We appreciate your help in keeping ServiceGo safe.",
                    "Complaint",
                    "fa-solid fa-shield-check",
                    complaint.ComplaintId,
                    Url.Action("Notifications", "Customer"));
                }
            }
            else if (string.Equals(
            complaint.ComplaintType,
            "Provider vs Customer",
            StringComparison.OrdinalIgnoreCase))
            {
                var complainant =
                await FindProviderByAnyIdAsync(complaint.ProviderId);

                if (complainant != null)
                {
                    AddProviderNotification(
                    complainant,
                    "Complaint Resolved",
                    $"Thank you for reporting this issue. After reviewing your complaint (ID: {complaint.ComplaintId}), we have blocked the reported user's account. Your complaint is now resolved. We appreciate your help in keeping ServiceGo safe.",
                    "Complaint",
                    "fa-solid fa-shield-check",
                    complaint.ComplaintId,
                    Url.Action("Notifications", "Provider"));
                }
            }

            await _context.SaveChangesAsync();

            string nextPage =
            string.Equals(
            blockedUserType,
            "Provider",
            StringComparison.OrdinalIgnoreCase)
            ? Url.Action("BlockedProviders", "Admin")
            ?? "/Admin/BlockedProviders"
            : Url.Action("BlockedCustomers", "Admin")
            ?? "/Admin/BlockedCustomers";

            return Json(new
            {
                success = true,
                message = $"{blockedUserType} blocked successfully. Complaint history has been kept.",
                blockedUserType = blockedUserType,
                complaintId = complaint.ComplaintId,
                id = complaint.Id,
                status = complaint.Status,
                nextPage = nextPage
            });
        }

        // Backward-compatible endpoint for any older page/script.
        // It now uses the same correct two-recipient warning flow.
        [HttpPost]
        public async Task<IActionResult> SendComplaintWarning(int id)
        {
            var complaint = await _context.Complaints
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

            if (complaint == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Complaint not found."
                });
            }

            return await SendWarning(
            new ComplaintWarningRequest
            {
                Id = complaint.Id,
                ComplaintId = complaint.ComplaintId ?? string.Empty,
                ComplaintType = complaint.ComplaintType ?? string.Empty,
                TargetUserId = complaint.TargetUserId ?? string.Empty,
                TargetUserName = complaint.TargetUserName ?? string.Empty
            });
        }

        // Wallet requests

        [HttpGet]
        public async Task<IActionResult> WalletRequests()
        {
            var requests =
            await _context.WalletRequests

            .Where(w =>
            w.Status == "Pending")

            .Select(w =>
            new WalletRequestViewModel
            {
                Id = w.Id,

                ProviderFormattedId =
            w.ProviderId,

                ProviderName =
            w.ProviderName,

                Amount =
            w.Amount,

                Status =
            w.Status,

                ReceiptImagePath =
            w.ReceiptImagePath
            })

            .ToListAsync();

            return View(requests);
        }

        // Update wallet status

        [HttpPost]
        public async Task<IActionResult> UpdateWalletStatus(
        int id,
        string actionType,
        decimal? actualAmount,
        string? rejectReason)
        {
            var walletRequest =
            await _context.WalletRequests
            .FindAsync(id);

            if (walletRequest == null)
            {
                return NotFound();
            }

            // Prevent the same wallet request from being processed twice.
            if (!string.Equals(
            walletRequest.Status,
            "Pending",
            StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("WalletRequests");
            }

            var walletProviderCode = (walletRequest.ProviderId ?? string.Empty).Trim();
            var walletProviderNumber = walletProviderCode.Replace("PRO-", "", StringComparison.OrdinalIgnoreCase);

            var provider =
            await _context.Register
            .FirstOrDefaultAsync(p =>
            p.ProviderId == walletProviderCode ||
            p.Id.ToString() == walletProviderNumber);

            // Approve

            if (string.Equals(
            actionType,
            "Approve",
            StringComparison.OrdinalIgnoreCase))
            {
                if (provider == null)
                {
                    return NotFound();
                }

                // Admin confirms the amount after checking the receipt.
                if (!actualAmount.HasValue || actualAmount.Value <= 0)
                {
                    return BadRequest(
                    "Please enter the actual amount received.");
                }

                walletRequest.Status = "Approved";

                // Important
                // Add the amount verified by Admin, not the amount
                // originally typed by the provider.
                provider.WalletBalance += actualAmount.Value;

                string requestedText = FormatWalletAmount(walletRequest.Amount);
                string verifiedText = FormatWalletAmount(actualAmount.Value);
                string balanceText = FormatWalletAmount(provider.WalletBalance);

                string approvedMessage = actualAmount.Value == walletRequest.Amount
                    ? $"Your wallet request of Rs. {requestedText} has been approved. Rs. {verifiedText} has been added to your wallet, and your new balance is Rs. {balanceText}."
                    : $"Your wallet request has been approved. After checking your receipt, we verified Rs. {verifiedText} (you requested Rs. {requestedText}), so Rs. {verifiedText} has been added to your wallet. Your new balance is Rs. {balanceText}.";

                AddProviderNotification(
                provider,
                "Wallet Top-Up Approved",
                approvedMessage,
                "Wallet",
                "fa-solid fa-circle-check",
                $"WALLET-{walletRequest.Id}",
                Url.Action("Notifications", "Provider"));
            }

            // Reject

            else if (string.Equals(
            actionType,
            "Reject",
            StringComparison.OrdinalIgnoreCase))
            {
                // The provider must be told why the request was rejected.
                string reason = (rejectReason ?? string.Empty).Trim().TrimEnd('.', ' ');

                if (string.IsNullOrWhiteSpace(reason))
                {
                    return BadRequest(
                    "Please select a reason for rejecting this request.");
                }

                if (reason.Length > 300)
                    reason = reason.Substring(0, 300);

                walletRequest.Status = "Rejected";

                AddProviderNotification(
                provider,
                "Wallet Top-Up Rejected",
                $"Your wallet request of Rs. {FormatWalletAmount(walletRequest.Amount)} could not be approved. Reason: {reason}. Please submit a new request with a clear receipt of your payment, or contact support if you think this is a mistake.",
                "Wallet",
                "fa-solid fa-circle-xmark",
                $"WALLET-{walletRequest.Id}",
                Url.Action("Notifications", "Provider"));
            }
            else
            {
                return BadRequest("Invalid wallet request action.");
            }

            await _context.SaveChangesAsync();

            return RedirectToAction("WalletRequests");
        }

        // Shows an amount without trailing zeros (30.00 becomes 30).
        private static string FormatWalletAmount(decimal amount)
        {
            return amount.ToString(
                "0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // Admin logout

        [HttpGet]
        public IActionResult Logout()
        {
            // Clear the admin session before returning to the public home page.
            HttpContext.Session.Clear();

            return Redirect("/");
        }

        // Request classes

        public class ComplaintActionRequest
        {
            public int Id { get; set; }
            public string ComplaintId { get; set; } = string.Empty;
        }

        public class ComplaintWarningRequest
        {
            public int Id { get; set; }
            public string ComplaintId { get; set; } = string.Empty;

            public string ComplaintType { get; set; } = string.Empty;

            public string TargetUserId { get; set; } = string.Empty;

            public string TargetUserName { get; set; } = string.Empty;
        }

        public class ComplaintBlockRequest
        {
            public int Id { get; set; }
            public string ComplaintId { get; set; } = string.Empty;

            public string ComplaintType { get; set; } = string.Empty;

            public string TargetUserId { get; set; } = string.Empty;
        }
    }
}