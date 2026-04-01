using System.ComponentModel.DataAnnotations;

namespace CommerceHub.API.Models;

// ── Checkout ─────────────────────────────────────────────────────────────────

public class CheckoutRequest
{
    [Required]
    public string CustomerId { get; set; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public List<CheckoutItem> Items { get; set; } = new();
}

public class CheckoutItem
{
    [Required]
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Quantity must be at least 1 — negative values are rejected.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }
}

// ── Order update ──────────────────────────────────────────────────────────────

public class UpdateOrderRequest
{
    /// <summary>New status to apply. Cannot update a Shipped order.</summary>
    public string? Status { get; set; }

    public string? CustomerId { get; set; }
}

// ── Stock adjustment ──────────────────────────────────────────────────────────

public class StockAdjustmentRequest
{
    /// <summary>
    /// Delta to apply. Positive = restock, negative = manual deduction.
    /// Cannot be zero (no-op). Operation is blocked if result would go below zero.
    /// </summary>
    /// <remarks>Zero delta is rejected as a no-op by the controller.</remarks>
    public int Delta { get; set; }
}

// ── Shared response ───────────────────────────────────────────────────────────

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string message) =>
        new() { Success = false, Message = message };
}
