// Controllers/AdminController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Linq;
using CardManagement.Data;
using CardManagement.Models;

namespace CardManagement.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        public AdminController(AppDbContext context) { _context = context; }

        // ── Resellers ─────────────────────────────────────────────────────────────

        [HttpGet("resellers")]
        public async Task<IActionResult> GetResellers() =>
            Ok(await _context.Resellers.Select(r => new { r.Id, r.Name }).ToListAsync());

        [HttpPost("resellers")]
        public async Task<IActionResult> AddReseller([FromBody] Reseller r)
        {
            _context.Resellers.Add(r);
            await _context.SaveChangesAsync();
            return Ok();
        }

        // ── Companies ─────────────────────────────────────────────────────────────

        [HttpGet("companies")]
        public async Task<IActionResult> GetCompanies() =>
            Ok(await _context.Companies.Select(c => new { c.Id, c.Name }).ToListAsync());

        [HttpPost("companies")]
        public async Task<IActionResult> AddCompany([FromBody] Company c)
        {
            _context.Companies.Add(c);
            await _context.SaveChangesAsync();
            return Ok(new { c.Id, c.Name });
        }

        [HttpDelete("companies/{id}")]
        public async Task<IActionResult> DeleteCompany(int id)
        {
            var company = await _context.Companies.FindAsync(id);
            if (company == null) return NotFound("Company not found.");

            // Cascade delete: DeviceCards → Devices → Units → Cards → Users
            var unitIds = await _context.Units
                .Where(u => u.CompanyId == id)
                .Select(u => u.Id)
                .ToListAsync();

            var deviceIds = await _context.Devices
                .Where(d => unitIds.Contains(d.UnitId))
                .Select(d => d.Id)
                .ToListAsync();

            if (deviceIds.Any())
            {
                var deviceCards = await _context.DeviceCards
                    .Where(dc => deviceIds.Contains(dc.DeviceId))
                    .ToListAsync();
                _context.DeviceCards.RemoveRange(deviceCards);

                var pollJobs = await _context.PollJobs
                    .Where(p => deviceIds.Contains(p.DeviceId))
                    .ToListAsync();
                _context.PollJobs.RemoveRange(pollJobs);

                var devices = await _context.Devices
                    .Where(d => deviceIds.Contains(d.Id))
                    .ToListAsync();
                _context.Devices.RemoveRange(devices);
            }

            if (unitIds.Any())
            {
                var units = await _context.Units
                    .Where(u => unitIds.Contains(u.Id))
                    .ToListAsync();
                _context.Units.RemoveRange(units);
            }

            var cards = await _context.Cards.Where(c => c.CompanyId == id).ToListAsync();
            _context.Cards.RemoveRange(cards);

            var users = await _context.Users.Where(u => u.CompanyId == id).ToListAsync();
            _context.Users.RemoveRange(users);

            _context.Companies.Remove(company);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Company '{company.Name}' and all related data deleted." });
        }

        // ── Users ─────────────────────────────────────────────────────────────────

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers() =>
            Ok(await _context.Users
                .Select(u => new {
                    u.Id,
                    u.Username,
                    u.Role,
                    u.CompanyId,
                    CompanyName = u.Company != null ? u.Company.Name : null
                })
                .ToListAsync());

        [HttpPost("users")]
        public async Task<IActionResult> AddUser([FromBody] User u)
        {
            _context.Users.Add(u);
            await _context.SaveChangesAsync();
            return Ok(new { u.Id, u.Username, u.Role });
        }

        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound("User not found.");

            // Prevent deleting the last admin
            if (user.Role == "Admin")
            {
                var adminCount = await _context.Users.CountAsync(u => u.Role == "Admin");
                if (adminCount <= 1)
                    return BadRequest("Cannot delete the last administrator account.");
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return Ok(new { message = $"User '{user.Username}' deleted." });
        }
    }
}