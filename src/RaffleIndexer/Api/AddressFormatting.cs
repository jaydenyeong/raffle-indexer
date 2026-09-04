using Nethereum.Util;

namespace RaffleIndexer.Api;

public static class AddressFormatting
{
    private static readonly AddressUtil Util = new();

    public static string? ToChecksum(string? lowercaseAddress) =>
        string.IsNullOrEmpty(lowercaseAddress) ? lowercaseAddress : Util.ConvertToChecksumAddress(lowercaseAddress);
}