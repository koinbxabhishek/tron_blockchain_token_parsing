using System;
using System.Numerics;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Nethereum.Web3;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using SimpleBase;
using System.Security.Cryptography;
using System.Text;

public partial class TransferEventDTO : TransferEventDTOBase { }

[Event("Transfer")]
public class TransferEventDTOBase : IEventDTO
{
    [Parameter("address", "_from", 1, true)]
    public virtual string From { get; set; }

    [Parameter("address", "_to", 2, true)]
    public virtual string To { get; set; }

    [Parameter("uint256", "_value", 3, false)]
    public virtual BigInteger Value { get; set; }
}

public class TransferScanner
{
    private Web3 _web3;
    private string _tokenAddress;
    private string _progressFile;
    private string _whitelistFile;
    private HashSet<string> _whitelist;
    private int _batchSize;
    private int _delayMs;
    private BigInteger _startBlock;
    private readonly string _transferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    public TransferScanner()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        _web3 = new Web3(config["RpcUrl"]);
        _tokenAddress = config["TokenAddress"];
        _batchSize = int.Parse(config["BatchSize"]);
        _delayMs = int.Parse(config["DelayMs"]);
        _progressFile = config["ProgressFile"];
        _whitelistFile = config["WhitelistFile"];

        _startBlock = LoadStartBlock(config["StartBlock"]);
        _whitelist = LoadWhitelist(_whitelistFile);
    }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                var latestBlock = (await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
                var endBlock = _startBlock + _batchSize;
                if (endBlock > latestBlock) endBlock = latestBlock;

                if (_startBlock > latestBlock)
                {
                    Console.WriteLine("⏳ Caught up. Waiting...");
                    await Task.Delay(_delayMs);
                    continue;
                }

                Console.WriteLine($"🔍 Tron Scanning {_startBlock} → {endBlock}");

                string ethtokenFormat = TronAddressConverter.ConvertTronToHex(_tokenAddress);

                var filter = new NewFilterInput
                {
                    FromBlock = new BlockParameter(new HexBigInteger(_startBlock)),
                    ToBlock = new BlockParameter(new HexBigInteger(endBlock)),
                    Address = new[] { ethtokenFormat },
                    Topics = new object[] { _transferTopic }
                };

                var logs = await WithRetry(() => _web3.Eth.Filters.GetLogs.SendRequestAsync(filter), 3);

                foreach (var log in logs)
                {
                    var decoded = Event<TransferEventDTO>.DecodeEvent(log);
                    if (decoded != null)
                    {
                        var from = decoded.Event.From.ToLower();
                        var to = decoded.Event.To.ToLower();
                        if (_whitelist.Contains(to))
                        {
                            decimal ethValue = Web3.Convert.FromWei(decoded.Event.Value, 6);
                            from = TronAddressConverter.ConvertHexToBase58(from);
                            to = TronAddressConverter.ConvertHexToBase58(to);
                            string cleanTxHash = log.TransactionHash.StartsWith("0x") ? log.TransactionHash.Substring(2) : log.TransactionHash;
                            await TransactionProcessor.ProcessTransaction(
                                cleanTxHash, from, to,
                                ethValue, log.BlockNumber.Value.ToString(),
                                "USDT", "Tron", _tokenAddress, 0, 0, "receive");

                            Console.WriteLine($"✅ {log.BlockNumber.Value} | {from} → {to} | {ethValue} | {cleanTxHash}");
                        }
                    }
                }

                _startBlock = endBlock + 1;
                File.WriteAllText(_progressFile, _startBlock.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                await Task.Delay(_delayMs);
            }
        }
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> func, int maxAttempts)
    {
        int attempts = 0;
        while (true)
        {
            try { return await func(); }
            catch
            {
                if (++attempts >= maxAttempts) throw;
                await Task.Delay(3000);
            }
        }
    }

    private BigInteger LoadStartBlock(string fallback)
    {
        if (File.Exists(_progressFile))
        {
            var text = File.ReadAllText(_progressFile);
            if (BigInteger.TryParse(text, out var val)) return val;
        }
        return BigInteger.Parse(fallback);
    }

    private HashSet<string> LoadWhitelist(string file)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(file))
        {
            //var trimmed = ConvertTronToHex(line.Trim().ToLower());
            var trimmed = TronAddressConverter.ConvertTronToHex(line.Trim());
            if (trimmed.StartsWith("0x")) set.Add(trimmed);
        }
        return set;
    }

    private string ConvertTronToHex(string tronAddress)
    {
        var decoded = Base58.Bitcoin.Decode(tronAddress).ToArray();
        var hex = BitConverter.ToString(decoded.Skip(1).ToArray()).Replace("-", "").ToLower();
        return "0x" + hex;
    }

}

public static class TronAddressConverter
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string ConvertTronToHex(string base58Address)
    {
        BigInteger intData = BigInteger.Zero;
        foreach (char c in base58Address)
        {
            int digit = Base58Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"Invalid Base58 character `{c}`");
            intData = intData * 58 + digit;
        }

        // Convert to byte array
        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Strip checksum and prefix
        if (bytes[0] != 0x41) throw new FormatException("Invalid Tron address prefix");
        var addressBytes = bytes.Skip(1).Take(20).ToArray(); // remove 0x41, keep 20 bytes

        return "0x" + BitConverter.ToString(addressBytes).Replace("-", "").ToLower();
    }

    public static string ConvertHexToBase58(string hex)
    {
        if (hex.StartsWith("0x")) hex = hex.Substring(2);

        byte[] hexBytes = Enumerable.Range(0, hex.Length / 2)
                                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                                    .ToArray();

        byte[] tronAddressBytes = new byte[21];
        tronAddressBytes[0] = 0x41; // Tron mainnet prefix
        Buffer.BlockCopy(hexBytes, 0, tronAddressBytes, 1, 20);

        byte[] hash = SHA256.Create().ComputeHash(SHA256.Create().ComputeHash(tronAddressBytes));
        byte[] checksum = hash.Take(4).ToArray();

        byte[] addressWithChecksum = new byte[25];
        Buffer.BlockCopy(tronAddressBytes, 0, addressWithChecksum, 0, 21);
        Buffer.BlockCopy(checksum, 0, addressWithChecksum, 21, 4);

        return Base58Encode(addressWithChecksum);
    }

    public static string Base58Encode(byte[] data)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        BigInteger intData = new BigInteger(data.Reverse().Concat(new byte[] { 0 }).ToArray());
        var result = new StringBuilder();

        while (intData > 0)
        {
            int remainder = (int)(intData % 58);
            intData /= 58;
            result.Insert(0, alphabet[remainder]);
        }

        foreach (var b in data)
        {
            if (b == 0x00)
                result.Insert(0, '1');
            else
                break;
        }

        return result.ToString();
    }
}

