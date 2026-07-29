using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;
using Microsoft.Extensions.Options;

namespace DevStack.API.PlatformLogic.MenuItemLogic;

// Business logic for menu items. Every public method follows the same shape:
//   var result = new ResultModel<T>();
//   try { … } catch (Exception error) { return _errorHandling.HandleException(error); }
public class MenuItemLogic : IMenuItemLogic
{
    private readonly IMenuItemRepository _repo;
    private readonly CloudinarySettings _cloudinary;
    private readonly IErrorHandling _errorHandling;

    public MenuItemLogic(
        IMenuItemRepository repo,
        IOptions<CloudinarySettings> cloudinary,
        IErrorHandling errorHandling)
    {
        _repo = repo;
        _cloudinary = cloudinary.Value;
        _errorHandling = errorHandling;
    }

    // ── READ ──────────────────────────────────────────────────────────────────

    public async Task<ResultModel<List<MenuItem>>> GetItemsAsync()
    {
        try
        {
            var items = await _repo.GetAllAsync();
            return ResultModel<List<MenuItem>>.Success(items);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<List<MenuItem>>(error);
        }
    }

    public async Task<ResultModel<MenuItem?>> GetItemAsync(int id)
    {
        try
        {
            var item = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException($"Menu item {id} not found.");

            return ResultModel<MenuItem?>.Success(item);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<MenuItem?>(error);
        }
    }

    // ── WRITE (create / edit) ─────────────────────────────────────────────────

    // Single write path: Id == 0 means "new", anything else means "edit that one".
    public async Task<ResultModel<MenuItem>> WriteItemAsync(MenuItem item)
    {
        try
        {
            item.Name = item.Name.Trim();
            item.Category = item.Category.Trim();
            if (item.Price < 0) item.Price = 0;

            if (item.Id == 0)
            {
                item.CreatedAt = DateTime.UtcNow.AddHours(2);
                var created = await _repo.AddAsync(item);
                return ResultModel<MenuItem>.Success(created);
            }

            // When editing, if the image changed, destroy the old one from Cloudinary
            // so orphaned images don't pile up.
            var existing = await _repo.GetByIdAsync(item.Id)
                ?? throw new KeyNotFoundException($"Menu item {item.Id} not found.");

            if (existing.ImagePublicId is not null
                && existing.ImagePublicId != item.ImagePublicId)
            {
                await DestroyCloudinaryImage(existing.ImagePublicId);
            }

            var updated = await _repo.UpdateAsync(item)
                ?? throw new KeyNotFoundException($"Menu item {item.Id} not found.");

            return ResultModel<MenuItem>.Success(updated);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<MenuItem>(error);
        }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    public async Task<ResultModel> DeleteItemAsync(int id)
    {
        try
        {
            var item = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException($"Menu item {id} not found.");

            // Destroy the Cloudinary image before removing the DB record.
            if (item.ImagePublicId is not null)
                await DestroyCloudinaryImage(item.ImagePublicId);

            await _repo.DeleteAsync(id);
            return ResultModel.Success();
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException(error);
        }
    }


    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task DestroyCloudinaryImage(string publicId)
    {
        try
        {
            using var client = new HttpClient();
            var auth = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_cloudinary.ApiKey}:{_cloudinary.ApiSecret}"));

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.cloudinary.com/v1_1/{_cloudinary.CloudName}/image/destroy")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { public_id = publicId }),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

            await client.SendAsync(request);
            // Best-effort: a failed image delete shouldn't block the DB delete.
        }
        catch
        {
            // Swallow — log in production, silently ignore in dev.
        }
    }
}
