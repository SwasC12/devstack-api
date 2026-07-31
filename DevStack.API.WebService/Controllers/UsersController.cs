using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class UsersController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public UsersController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    public record CreateUserRequest(string Username, string Password, string DisplayName, string Role, string? Pin);
    public record SetPinRequest(string Pin);

    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        var users = await _db.Users
            .Where(u => u.ShopId == _currentShop.ShopId)
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, HasPin = u.PinHash != null })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult> Create(CreateUserRequest request)
    {
        // Usernames are unique per shop, so two shops can both have an "admin".
        if (await _db.Users.AnyAsync(u => u.ShopId == _currentShop.ShopId && u.Username == request.Username))
            return BadRequest(new { error = "Username already exists." });

        var policyError = PasswordPolicy.Validate(request.Password);
        if (policyError is not null) return BadRequest(new { error = policyError });
        if (!IsValidPin(request.Pin)) return BadRequest(new { error = "PIN must be 4-6 digits." });

        var user = new AppUser
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            PinHash = string.IsNullOrEmpty(request.Pin) ? null : BCrypt.Net.BCrypt.HashPassword(request.Pin),
            DisplayName = request.DisplayName,
            Role = request.Role is "cashier" or "admin" ? request.Role : "cashier",
            ShopId = _currentShop.ShopId
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAll), new { id = user.Id }, new { user.Id, user.Username, user.DisplayName, user.Role });
    }

    // Set / change a staff member's PIN (the fast cashier sign-in).
    [HttpPost("{id:int}/pin")]
    public async Task<ActionResult> SetPin(int id, SetPinRequest request)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null || user.ShopId != _currentShop.ShopId) return NotFound();

        if (!IsValidPin(request.Pin)) return BadRequest(new { error = "PIN must be 4-6 digits." });

        user.PinHash = BCrypt.Net.BCrypt.HashPassword(request.Pin);
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static bool IsValidPin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsDigit);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null || user.ShopId != _currentShop.ShopId) return NotFound();

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
