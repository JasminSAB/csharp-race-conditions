using System.Collections.Concurrent;

namespace RaceConditions.CheckThenAct;

/// <summary>Books the discount: an order line, a payment provider call, an email.</summary>
public interface IDiscountLedger
{
    Task GrantAsync(string code, string customer);
}

public interface ICouponRedemption
{
    Task<bool> RedeemAsync(string code, string customer);
}

/// <summary>
/// Race: the check ("not redeemed yet") and the act ("mark redeemed") are separated by an await.
/// Two customers both pass the check before either one marks the code, and a single-use coupon
/// is granted twice.
/// </summary>
public sealed class CouponRedemption(IDiscountLedger ledger) : ICouponRedemption
{
    private readonly HashSet<string> _redeemed = [];

    public async Task<bool> RedeemAsync(string code, string customer)
    {
        if (_redeemed.Contains(code)) return false;

        await ledger.GrantAsync(code, customer);
        _redeemed.Add(code);
        return true;
    }
}

/// <summary>
/// Fix: claim first, in one atomic step, and let the result decide who acts. TryAdd returns true
/// for exactly one caller. If granting fails, the claim is released so the code stays usable.
/// </summary>
public sealed class SafeCouponRedemption(IDiscountLedger ledger) : ICouponRedemption
{
    private readonly ConcurrentDictionary<string, string> _claims = new();

    public async Task<bool> RedeemAsync(string code, string customer)
    {
        if (!_claims.TryAdd(code, customer)) return false;

        try
        {
            await ledger.GrantAsync(code, customer);
            return true;
        }
        catch
        {
            _claims.TryRemove(code, out _);
            throw;
        }
    }
}
