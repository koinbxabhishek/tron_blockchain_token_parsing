using System;
using System.IO;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("🔄 Starting ERC20 Transfer Scanner WebJob...");

        try
        {
            var scanner = new TransferScanner();
            await scanner.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fatal error: {ex.Message}");
        }
    }
}
