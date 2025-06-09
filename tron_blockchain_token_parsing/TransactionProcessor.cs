using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Nethereum.Web3;
using System.Numerics;
using System.Text.Json;

class TransactionProcessor
{
    private static string connectionString = "Server=tcp:koinbx-blockchain.database.windows.net,1433;Initial Catalog=koinbx_blockchain;User ID=eqNjI7YVihZr77JT2Qtkh5NMFZtOGk;Password=Mj0$nfyb00LfoG@ca&TQOkzC72*18f;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;\r\n";
    private static string apiUrl = "https://captureapi.koinbx.com/customRouter/customWebhookFunction";

    public TransactionProcessor() {
        var config = new ConfigurationBuilder()
               .AddJsonFile("appsettings.json")
               .Build();

        connectionString= config["connectionString"];
        apiUrl =config["apiUrl"];

    }
    public static async Task ProcessTransaction(
        string transactionHash, string fromAddress, string toAddress, decimal value, string block, string coin,
        string network, string contractAddress, decimal fee, int confirmation, string type)
    {
        try
        {
            // Prepare payload
            var requestData = new
            {
                txid = transactionHash,
                Type = type,
                fee = fee,
                amount = value,
                fromaddress = fromAddress,
                toaddress = toAddress,
                block = block,
                coin = coin,
                Network = network,
                tokenContractAddress = contractAddress,
                blockConfirmation = confirmation
            };
            string requestJson = JsonSerializer.Serialize(requestData);

            // Insert transaction into the database
            Int32 transactionId = InsertTransaction(transactionHash, fromAddress, toAddress, value, block, coin, network, contractAddress, fee, confirmation, type, requestJson).Result;

            if (transactionId > 0)
            {
                var (statusCode, responseJson) = await CallApi(requestJson);

                await UpdateTransactionApiStatus(transactionId, statusCode, responseJson);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static async Task<Int32> InsertTransaction(string transactionHash, string fromAddress, string toAddress, decimal amount, string block, string coin, string network, string contractAddress, decimal fee, int confirmation, string type, string requestJson)
    {
        Int32 insertedId = 0;

        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            string sql = @"
                INSERT INTO CryptoDepositV4 
(TransactionHash, FromAddress, ToAddress, Amount, Block, Coin, Network, ContractAddress, Fees, Confirmation, Type, ApiStatus, ApiData, Payload, CreatedOnUtc)
OUTPUT INSERTED.Id
SELECT 
    @TransactionHash, @FromAddress, @ToAddress, @Amount, @Block, @Coin, @Network,
    @ContractAddress, @Fees, @Confirmation, @Type, 0, NULL, @Payload, GETUTCDATE()
WHERE NOT EXISTS (
    SELECT 1
    FROM CryptoDepositV4
    WHERE TransactionHash = @TransactionHash
      AND Coin = @Coin
      AND Network = @Network
)"
;

            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@TransactionHash", transactionHash);
                command.Parameters.AddWithValue("@FromAddress", fromAddress);
                command.Parameters.AddWithValue("@ToAddress", toAddress);
                command.Parameters.AddWithValue("@Amount", amount);
                command.Parameters.AddWithValue("@Block", block);
                command.Parameters.AddWithValue("@Coin", coin);
                command.Parameters.AddWithValue("@Network", network);
                command.Parameters.AddWithValue("@ContractAddress", contractAddress ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Fees", fee);
                command.Parameters.AddWithValue("@Confirmation", confirmation);
                command.Parameters.AddWithValue("@Type", type);
                command.Parameters.AddWithValue("@Payload", requestJson);

                var result = await command.ExecuteScalarAsync();
                insertedId = result != null ? Convert.ToInt32(result) : 0;
            }
        }

        return insertedId;
    }

    private static async Task<(int, string)> CallApi(string requestJson)
    {
        using (var httpClient = new HttpClient())
        {
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage response = await httpClient.PostAsync(apiUrl, content);
            string responseJson = await response.Content.ReadAsStringAsync();

            return ((int)response.StatusCode, responseJson);
        }
    }

    private static async Task UpdateTransactionApiStatus(long transactionId, int statusCode, string responseJson)
    {
        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            string sql = @"
                UPDATE CryptoDepositV4
                SET ApiStatus = 1, ApiStatusCode = @ApiStatusCode, ApiData = @ApiData, ModifiedOnUtc = GETUTCDATE()
                WHERE Id = @TransactionId";

            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@ApiStatusCode", statusCode);
                command.Parameters.AddWithValue("@ApiData", responseJson);
                command.Parameters.AddWithValue("@TransactionId", transactionId);

                await command.ExecuteNonQueryAsync();
            }
        }
    }
}

