using BlockChain_FP_ITStep.Services;
using Microsoft.AspNetCore.Mvc;

namespace BlockChain_FP_ITStep.Controllers
{
    public class WalletController : Controller
    {
        private readonly BlockChainService _bcService;

        public WalletController(BlockChainService bcService)
        {
            _bcService = bcService;
        }

        // Wallet details page
        [HttpGet("/wallet/{address}")]
        public async Task<IActionResult> Index(string address, string nodeId = "A")
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest("Wallet address required");

            var balances = await _bcService.GetBalances(nodeId, includeMempool: true);
            balances.TryGetValue(address, out var balance);

            var txs = await _bcService.GetTransactionsByWalletAsync(address, nodeId);

            var wallet = _bcService.Wallets.ContainsKey(address)
                ? _bcService.Wallets[address]
                : null;

            ViewBag.Address = address;
            ViewBag.Balance = balance;
            ViewBag.Wallet = wallet;

            return View(txs);
        }


    }
}

