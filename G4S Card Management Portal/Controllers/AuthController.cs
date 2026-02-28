// Controllers/AuthController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using CardManagement.Data;

namespace CardManagement.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        public AuthController(AppDbContext context) { _context = context; }

        public class LoginRequest
        {
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest("Username and password are required.");

            // Simple plaintext comparison — swap for BCrypt in production
            var user = await _context.Users
                .Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.Username == req.Username && u.PasswordHash == req.Password);

            if (user == null)
                return Unauthorized("Invalid username or password.");

            return Ok(new
            {
                user.Id,
                user.Username,
                user.Role,
                user.CompanyId,
                CompanyName = user.Company?.Name
            });
        }

        [HttpGet("me")]
        public async Task<IActionResult> Me([FromQuery] int userId)
        {
            var user = await _context.Users
                .Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null) return Unauthorized();

            return Ok(new
            {
                user.Id,
                user.Username,
                user.Role,
                user.CompanyId,
                CompanyName = user.Company?.Name
            });
        }
    }
}