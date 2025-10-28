using BlockChain_FP_ITStep.Models;
using BlockChain_FP_ITStep.Services;
using Microsoft.AspNetCore.Mvc;

namespace BlockChain_FP_ITStep.Controllers
{
    public class BlockChainController(BlockChainService bcService) : Controller
    {
        private readonly BlockChainService _bcService = bcService;

        public static CancellationTokenSource? _cts;

        public async Task<IActionResult> Index()
        {
            ViewBag.AlertMessage = TempData["AlertMessage"];
            ViewBag.AlertType = TempData["AlertType"];

            var validatedBlocks = await _bcService.GetValidatedBlocksAsync();
            var isSignatureValid = await _bcService.GetSignatureValidationAsync();

            var model = validatedBlocks.Select((block, i) => new BlockValidationViewModel
            {
                Block = block.Block,
                IsValid = block.IsValid,                        // цепочка
                IsSignatureValid = isSignatureValid[i].IsValid  // подпись
            }).ToList();

            ViewBag.IsChainValid = model.All(b => b.IsValid);
            ViewBag.Difficulty = BlockChainService.Difficulty;
            return View(model);
        }



        [HttpPost]
        public async Task<IActionResult> Add(string data, string privateKey)
        {
            if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(privateKey))
            {
                TempData["AlertMessage"] = "Please enter both data and private key.";
                TempData["AlertType"] = "danger";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                long ms = await _bcService.AddBlockAsync(data, privateKey);
                TempData["AlertMessage"] = "Block successfully added.";
                TempData["AlertType"] = "success";
            }
            catch (Exception ex)
            {
                TempData["AlertMessage"] = "Error: " + ex.Message;
                TempData["AlertType"] = "danger";
            }

            return RedirectToAction("Index");
        }

        // маршрут для генерации ключа
        [HttpGet]
        public IActionResult GenerateKey()
        {
            var privateKey = _bcService.GeneratePrivateKeyXml();
            if (string.IsNullOrWhiteSpace(privateKey))
                return BadRequest("Error generating key");

            return Content(privateKey, "text/plain");
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


        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var block = await _bcService.GetBlockByIdAsync(id);
            if (block == null) return NotFound();
            return View(block);
        }

        [HttpPost]
        public IActionResult SetDifficulty(int difficulty)
        {
            if (difficulty < 1) difficulty = 1;
            if (difficulty > 6) difficulty = 6;
            BlockChainService.Difficulty =  difficulty;
            return RedirectToAction("Index");
        }


        // Mining
        [HttpPost]
        public IActionResult StartMining(string data, string privateKey)
        {
            if (string.IsNullOrWhiteSpace(privateKey))
                return BadRequest("Private key required");

            if (string.IsNullOrWhiteSpace(data))
                data = "Empty";

            _cts = new CancellationTokenSource();
            var progress = new Progress<int>(_ => { });

            Task.Run(async () =>
            {
                await _bcService.MineAsync(data, privateKey, _cts.Token, progress);
            });

            return Ok();
        }

        [HttpPost]
        public IActionResult StopMining()
        {
            _cts?.Cancel();
            return Ok();
        }
    }
}
