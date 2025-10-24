using BlockChain_FP_ITStep.Services;
using Microsoft.AspNetCore.Mvc;

namespace BlockChain_FP_ITStep.Controllers
{
    public class BlockChainController(BlockChainService bcService) : Controller
    {
        private readonly BlockChainService _bcService = bcService;

        public async Task<IActionResult> Index()
        {
            var validatedBlocks = await _bcService.GetValidatedBlocksAsync();
            ViewBag.IsChainValid = await _bcService.IsValidAsync();
            return View(validatedBlocks);
        }


        [HttpPost]
        public async Task<IActionResult> Add(string data)
        {
            await _bcService.AddBlockAsync(data);
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int index)
        {
            var block = await _bcService.GetBlockByIndexAsync(index);
            if (block == null) return NotFound();
            return View(block);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int index, string data, string signature)
        {
            var result = await _bcService.EditBlockAsync(index, data, signature);
            if (!result) return NotFound();
            return RedirectToAction(nameof(Index));
        }


        [HttpGet]
        public async Task<IActionResult> SearchByHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return RedirectToAction(nameof(Index));

            var blocks = await _bcService.GetAllBlocksAsync();
            var found = blocks.FirstOrDefault(b => b.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));    //  Ordinal -> Сравнивает побайтово символы, без учёта языка и культуры.  + IgnoreCase

            if (found == null)
            {
                ViewBag.SearchMessage = "Block not found.";
                ViewBag.IsChainValid = await _bcService.IsValidAsync();
                var validatedBlocks = await _bcService.GetValidatedBlocksAsync();
                return View("Index", validatedBlocks);
            }

            return View(found);
        }
    }
}
