using WellWorksIngestion.Api.Models;
using WellWorksIngestion.Api.Services;

namespace WellWorksIngestion.Api.Validators;

/// <summary>
/// Stateless validator — registered as Singleton.
/// All rules are pure C#; no DB calls, no async.
/// </summary>
public sealed class TransactionValidator : ITransactionValidator
{
    private const int MaxIdLength     = 64;
    private const int MaxMemberLength = 64;

    public ValidationResult Validate(TransactionDto record)// return right away upon error, no need to validate further. This could be changed if more verbose logging is desired to list all the errors a single record has if more then one exist
    {
        // TransactionID checks
        if (string.IsNullOrWhiteSpace(record.TransactionID))
            return ValidationResult.Fail("TransactionID is required.");

        if (record.TransactionID.Trim().Length > MaxIdLength)
            return ValidationResult.Fail($"TransactionID exceeds {MaxIdLength} characters.");

        // MemberID checks
        if (string.IsNullOrWhiteSpace(record.MemberID))
            return ValidationResult.Fail("MemberID is required.");

        if (record.MemberID.Trim().Length > MaxMemberLength)
            return ValidationResult.Fail($"MemberID exceeds {MaxMemberLength} characters.");

        // Date checks
        if (record.TransactionDate == default)
            return ValidationResult.Fail("TransactionDate is required.");

        if (record.TransactionDate > DateTime.UtcNow.AddMinutes(5))
            return ValidationResult.Fail("TransactionDate cannot be in the future.");

        // Amount checks — 0 is allowed (reversal/adjustment), negative is not
        if (record.TransactionAmount < 0)
            return ValidationResult.Fail("TransactionAmount cannot be negative.");

        return ValidationResult.Ok();
    }
}
