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
    private readonly ICategoryRepository _categoryRepo;
    private readonly CloudinarySettings _cloudinary;
    private readonly IErrorHandling _errorHandling;
    private readonly ICurrentShop _currentShop;

    public MenuItemLogic(
        IMenuItemRepository repo,
        ICategoryRepository categoryRepo,
        IOptions<CloudinarySettings> cloudinary,
        IErrorHandling errorHandling,
        ICurrentShop currentShop)
    {
        _repo = repo;
        _categoryRepo = categoryRepo;
        _cloudinary = cloudinary.Value;
        _errorHandling = errorHandling;
        _currentShop = currentShop;
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
            if (item.Name.Length == 0)
                throw new ArgumentException("Item name cannot be empty.");

            // SKU/barcode: optional, trimmed, unique per shop (case-insensitive).
            item.Sku = string.IsNullOrWhiteSpace(item.Sku) ? null : item.Sku.Trim();
            if (item.Sku is not null)
            {
                if (item.Sku.Length > 40)
                    throw new ArgumentException("SKU is too long (max 40 characters).");
                if (await _repo.SkuExistsAsync(item.Sku, item.Id))
                    throw new ArgumentException($"SKU '{item.Sku}' is already used by another item.");
            }

            item.Category = item.Category.Trim();
            if (item.Price < 0) item.Price = 0;

            // Sizes: trim names, clamp prices, enforce sanity limits. An item
            // with sizes sells at size prices only - the base Price is ignored
            // by the order path once sizes exist.
            var sizes = item.Sizes ?? [];
            if (sizes.Count > 6)
                throw new ArgumentException("An item can have at most 6 sizes.");
            var seen = new HashSet<string>();
            foreach (var s in sizes)
            {
                s.Name = s.Name.Trim();
                if (s.Name.Length == 0)
                    throw new ArgumentException("Every size needs a name.");
                if (!seen.Add(s.Name.ToLowerInvariant()))
                    throw new ArgumentException($"Duplicate size '{s.Name}'.");
                if (s.Price < 0) s.Price = 0;
            }

            // Modifier groups: names unique, options unique per group, deltas
            // clamped, sane limits.
            var groups = item.ModifierGroups ?? [];
            if (groups.Count > 5)
                throw new ArgumentException("An item can have at most 5 modifier groups.");
            var groupNames = new HashSet<string>();
            foreach (var g in groups)
            {
                g.Name = g.Name.Trim();
                if (g.Name.Length == 0)
                    throw new ArgumentException("Every modifier group needs a name.");
                if (!groupNames.Add(g.Name.ToLowerInvariant()))
                    throw new ArgumentException($"Duplicate modifier group '{g.Name}'.");
                if (g.Modifiers.Count > 10)
                    throw new ArgumentException($"Group '{g.Name}' can have at most 10 options.");
                var modNames = new HashSet<string>();
                foreach (var m in g.Modifiers)
                {
                    m.Name = m.Name.Trim();
                    if (m.Name.Length == 0)
                        throw new ArgumentException($"Every option in '{g.Name}' needs a name.");
                    if (!modNames.Add(m.Name.ToLowerInvariant()))
                        throw new ArgumentException($"Duplicate option '{m.Name}' in '{g.Name}'.");
                    if (m.PriceDelta < 0) m.PriceDelta = 0;
                }
            }

            // Items always belong to the current shop; the client can't change it.
            item.ShopId = _currentShop.ShopId;

            // Categories are a real table now, but items still carry a plain
            // string. Keep the two in sync: if the item names a category that
            // doesn't exist yet, create it so the Categories tab always reflects
            // what items actually use (and the admin dropdown can find it). The
            // CategoryId FK is set here too - the string is the display label,
            // the id is the relationship.
            if (item.Category.Length > 0)
            {
                var category = await _categoryRepo.GetByNameAsync(item.Category);
                if (category is null)
                {
                    category = await _categoryRepo.AddAsync(new Category
                    {
                        Name = item.Category,
                        ShopId = _currentShop.ShopId,
                        CreatedAt = DateTime.UtcNow.AddHours(2)
                    });
                }
                item.CategoryId = category.Id;
            }
            else
            {
                item.CategoryId = null;
            }

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
