using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Features.Idempotency;
using Ledger.Features.OpenAccount;
using Ledger.Features.RecordEntry;
using Ledger.Features.ReverseEntry;

namespace Ledger.UnitTests;

public sealed class RequestHashTests
{
    private static readonly Guid Account = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid OtherAccount = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Customer = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid OtherCustomer = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid Entry = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly Guid Key = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Caller Service = Caller.Service("payments-hub");

    // Cada variante muda um único campo em relação à base; todas devem mudar o hash.
    public static TheoryData<string> Variants => new(VariantCommands.Keys.ToArray());

    private static Dictionary<string, IIdempotentCommand> VariantCommands => new()
    {
        ["debit instead of credit"] = Credit() with { Type = EntryType.Debit },
        ["reversal"] = new ReverseEntryCommand(Account, Entry, "REF-1", Service, Key),
        ["opening"] = new OpenAccountCommand(Account, Service, Key),
        ["account"] = Credit() with { AccountId = OtherAccount },
        ["amount"] = Credit() with { Amount = Money.Of(10.01m) },
        ["category"] = Credit() with { Category = "TED_IN" },
        ["external reference"] = Credit() with { ExternalReference = "REF-2" },
        ["delegated mode"] = Credit() with { Caller = Caller.Delegated("payments-hub", Customer) },
    };

    [Fact]
    public void HashIsSha256()
    {
        RequestHash.Of(Credit()).Length.ShouldBe(32);
    }

    [Fact]
    public void SameInterpretedValuesGiveSameHash()
    {
        // `10.0` e `10.00` são o mesmo dinheiro com escalas diferentes no `decimal`: só a normalização os iguala.
        var oneDecimal = Credit() with { Amount = Money.Of(10.0m) };
        var twoDecimals = Credit() with { Amount = Money.Of(10.00m) };

        oneDecimal.Amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("10.0");
        RequestHash.Of(oneDecimal).ShouldBe(RequestHash.Of(twoDecimals));
        RequestHash.Of(Credit()).ShouldBe(RequestHash.Of(Credit()));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void EachFieldChangesTheHash(string variant)
    {
        RequestHash.Of(VariantCommands[variant]).ShouldNotBe(RequestHash.Of(Credit()), variant);
    }

    [Fact]
    public void ReversedEntryChangesTheHash()
    {
        var reversal = new ReverseEntryCommand(Account, Entry, "REF-1", Service, Key);
        var otherEntry = reversal with { EntryId = Guid.Parse("88888888-8888-4888-8888-888888888888") };

        RequestHash.Of(otherEntry).ShouldNotBe(RequestHash.Of(reversal));
    }

    [Fact]
    public void OpeningCustomerChangesTheHash()
    {
        var opening = new OpenAccountCommand(Customer, Service, Key);

        RequestHash.Of(opening with { CustomerId = OtherCustomer }).ShouldNotBe(RequestHash.Of(opening));
    }

    [Fact]
    public void DelegatedCustomerChangesTheHash()
    {
        var delegated = Credit() with { Caller = Caller.Delegated("channel-app", Customer) };
        var otherCustomer = Credit() with { Caller = Caller.Delegated("channel-app", OtherCustomer) };

        RequestHash.Of(otherCustomer).ShouldNotBe(RequestHash.Of(delegated));
    }

    // A chave e o client são o endereço da linha, não o conteúdo: não entram no hash.
    [Fact]
    public void KeyAndClientDoNotChangeTheHash()
    {
        var otherKey = Credit() with { IdempotencyKey = Guid.Parse("99999999-9999-4999-8999-999999999999") };
        var otherClient = Credit() with { Caller = Caller.Service("backoffice") };

        RequestHash.Of(otherKey).ShouldBe(RequestHash.Of(Credit()));
        RequestHash.Of(otherClient).ShouldBe(RequestHash.Of(Credit()));
    }

    private static RecordEntryCommand Credit() => new(Account, EntryType.Credit, Money.Of(10m), "PIX_IN", "REF-1", Service, Key);
}
