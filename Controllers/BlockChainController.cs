using BlockChain_FP_ITStep.Models;
using BlockChain_FP_ITStep.Models.Contracts;
using BlockChain_FP_ITStep.Services;
using Microsoft.AspNetCore.Mvc;

namespace BlockChain_FP_ITStep.Controllers
{
    public class BlockChainController(BlockChainService bcService) : Controller
    {
        private readonly BlockChainService _bcService = bcService;

        public static CancellationTokenSource? _cts;

        public async Task<IActionResult> Index(string nodeId = "A")
        {
            // TODO:  ViewBags to ViewModel !

            ViewBag.AlertMessage = TempData["AlertMessage"];
            ViewBag.AlertType = TempData["AlertType"];

            var validatedBlocks = await _bcService.GetValidatedBlocksAsync(nodeId);
            var isSignatureValid = await _bcService.GetSignatureValidationAsync(nodeId);

            var model = validatedBlocks.Select((block, i) => new BlockValidationViewModel
            {
                Block = block.Block,
                IsValid = block.IsValid,                            // цепочка
                IsSignatureValid = isSignatureValid[i].IsValid      // подпись
            }).ToList();

            // добавляет reward к каждому блоку (для вывода инфы о блоках в UI)
            foreach (var vm in model)
                vm.Reward = _bcService.GetBlockReward(vm.Block.Index);

            // Circulating Supply (исключая genesis)
            ViewBag.CirculatingSupply = model
                .Where(m => m.Block.Index > 0)
                .Sum(m => m.Reward);

            // reward для следующего блока
            var lastIndex = model.Max(m => m.Block.Index);
            ViewBag.CurrentReward = _bcService.GetBlockReward(lastIndex + 1);

            ViewBag.IsChainValid = model.All(b => b.IsValid);
            ViewBag.Difficulty = BlockChainService.Difficulty;

            ViewBag.Mempool = _bcService.Mempool.Where(t => t.NodeId == nodeId).ToList();
            ViewBag.MempoolCount = ((List<Transaction>)ViewBag.Mempool).Count;

            ViewBag.Wallets = _bcService.Wallets.Values.ToList();
            ViewBag.Balances = await _bcService.GetBalances(nodeId, true);

            ViewBag.Nodes = await _bcService.GetNodeIdsAsync();     // список всех нод в БД
            ViewBag.NodeId = nodeId;     // текущая выбранная нода

            // Добавим контракты для UI
            ViewBag.Contracts = _bcService.Contracts;

            // Staking - smart contract
            ViewBag.PrivateKeyStakingContract = _bcService.PrivateKeyXmlStakingContract;
            ViewBag.PublicKeyStakingContract = _bcService.PublicKeyXmlStakingContract;
            ViewBag.StakingAddress = _bcService.StakingContractAddress;

            // Penalty Staking - smart contract
            ViewBag.PenaltyStakingAddress = _bcService.PenaltyStakingContractAddress;

            return View(model);
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
        public IActionResult GenerateKeyPair()
        {
            var privateKey = _bcService.GeneratePrivateKeyXml();
            var publicKey = _bcService.GetPublicKeyFromPrivate(privateKey);

            if (privateKey == null || publicKey == null)
                return BadRequest("Key generation failed");

            return Json(new { privateKey, publicKey });
        }


        [HttpGet]
        public async Task<IActionResult> Edit(int index)
        {
            var block = await _bcService.GetBlockByIndexAsync(index);
            if (block == null) return NotFound();
            return View(block);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int index, string signature)
        {
            var result = await _bcService.EditBlockAsync(index, signature);
            if (!result) return NotFound();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> SearchByHash(string hash, string nodeId = "A")
        {
            if (string.IsNullOrWhiteSpace(hash))
                return RedirectToAction(nameof(Index));

            var blocks = await _bcService.GetAllBlocksAsync(nodeId);
            var found = blocks.FirstOrDefault(b => b.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));    //  Ordinal -> Сравнивает побайтово символы, без учёта языка и культуры.  + IgnoreCase

            if (found == null)
            {
                ViewBag.SearchMessage = "Block not found.";
                ViewBag.IsChainValid = await _bcService.IsValidAsync(nodeId);
                var validatedBlocks = await _bcService.GetValidatedBlocksAsync(nodeId);
                return View("Index", validatedBlocks);
            }

            return View(found);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id, string nodeId = "A")
        {
            var block = await _bcService.GetBlockByIdWithTransactionsAsync(id);
            if (block == null) return NotFound();

            // передаем индекс последнего блока для подсчёта подтверждений
            var blocks = await _bcService.GetAllBlocksAsync(nodeId);
            ViewBag.LastBlockIndex = blocks.Max(b => b.Index);

            ViewBag.StakingAddress = _bcService.StakingContractAddress;
            ViewBag.PenaltyStakingAddress = _bcService.PenaltyStakingContractAddress;

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

        // Old mining with Cancellation Token.
        //[HttpPost]
        //public IActionResult StartMining(string privateKey)
        //{
        //    if (string.IsNullOrWhiteSpace(privateKey))
        //        return BadRequest("Private key required");

        //    _cts = new CancellationTokenSource();
        //    var progress = new Progress<int>(_ => { });

        //    Task.Run(async () =>
        //    {
        //        await _bcService.MineAsync(privateKey, _cts.Token, progress);
        //    });

        //    return Ok();
        //}

        [HttpPost]
        public IActionResult StopMining()
        {
            _cts?.Cancel();
            return Ok();
        }

        [HttpPost]
        public IActionResult RegisterWallet(string publicKeyXml, string displayName, string nodeId)
        {
            _bcService.RegisterWallet(publicKeyXml, displayName);

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpPost]
        public IActionResult CreateTransaction(string fromAddress, string toAddress, decimal amount, decimal fee, string privateKey, string note, string nodeId = "A")
        {
            var tx = new Transaction
            {
                NodeId = nodeId,
                FromAddress = fromAddress,
                ToAddress = toAddress,
                Amount = amount,
                Fee = fee,
                Note = note
            };

            tx.Signature = BlockChainService.SignPayload(tx.CanonicalPayload(), privateKey);

            try 
            { 
                _bcService.CreateTransaction(tx, nodeId); 
            }
            catch (Exception ex) 
            { 
                TempData["Error"] = ex.Message; 
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpPost]
        public async Task<IActionResult> MinePending(string privateKey, string nodeId = "A")
        {
            try 
            { 
                await _bcService.MinePendingAsync(privateKey, nodeId);
                await _bcService.BroadcastChainAsync(nodeId);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.InnerException?.Message ?? ex.Message;
                return RedirectToAction("Index", new { nodeId });
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpPost]
        public async Task<IActionResult> DemoSetup()
        {
            var (Ivan, prvKey) = _bcService.CreateWallet("Ivan");
            var (Taras, prvKey2) = _bcService.CreateWallet("Taras");
            var nodeId = "A";

            decimal amount = 3.0m;
            decimal fee = 0.1m;

            var tx = new Transaction
            {
                NodeId = nodeId,
                FromAddress = Ivan.Address,
                ToAddress = Taras.Address,
                Amount = amount,
                Fee = fee,
                Note = "Payment for services"
            };

            for (int i = 0; i < 5; i++)
            {
                await MinePending(prvKey, nodeId);
                await MinePending(prvKey2, nodeId);
            }

            var sig = BlockChainService.SignPayload(tx.CanonicalPayload(), prvKey);
            tx.Signature = sig;

            try
            {
                _bcService.CreateTransaction(tx, nodeId);
            }
            catch (Exception)
            {
                TempData["Error"] = "Demo transaction failed.";
                return RedirectToAction("Index", new { nodeId });
            }

            TempData["Success"] = "Demo completed!";
            return RedirectToAction("Index", new { nodeId });
        }


        // Staking
        [HttpPost]
        public IActionResult Stake(string fromAddress, decimal amount, decimal fee, string privateKey, string nodeId)
        {
            try
            {
                if (amount <= 0)
                    throw new Exception("Stake amount must be positive");

                if (fee < 0)
                    throw new Exception("Fee cannot be negative");

                var tx = new Transaction
                {
                    FromAddress = fromAddress,
                    ToAddress = _bcService.StakingContractAddress,
                    Amount = amount,
                    Fee = fee,
                    Note = "Stake tokens",
                    NodeId = nodeId
                };

                tx.Signature = BlockChainService.SignPayload(tx.CanonicalPayload(), privateKey);

                _bcService.CreateTransaction(tx, nodeId);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpPost]
        public IActionResult WithdrawFromStake(string userAddress, decimal amount, string nodeId)
        {
            try
            {
                var tx = new Transaction
                {
                    FromAddress = _bcService.StakingContractAddress,
                    ToAddress = userAddress,
                    Amount = amount,
                    Fee = 0m,
                    Note = "Withdraw from stake",
                    NodeId = nodeId
                };

                tx.Signature = BlockChainService.SignPayload(
                    tx.CanonicalPayload(),
                    _bcService.PrivateKeyXmlStakingContract
                );

                _bcService.CreateTransaction(tx, nodeId);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpGet]
        public IActionResult GetStakeInfo(string userAddress, string nodeId = "A")
        {
            var chain = _bcService.GetChainAsync(nodeId).Result;
            var lastIndex = chain.Last().Index;

            var (stake, reward, total) = _bcService.GetStakeSummary(userAddress, lastIndex);

            return Json(new
            {
                stake,
                reward,
                total,
                formatted = $"{stake} staked, {reward} reward (total: {total})"
            });
        }


        // Penalty Staking
        [HttpPost]
        public IActionResult PenaltyStake(string fromAddress, decimal amount, decimal fee, string privateKey, string nodeId)
        {
            try
            {
                if (amount <= 0)
                    throw new Exception("Amount must be positive");
                if (fee < 0)
                    throw new Exception("Fee cannot be negative");

                var tx = new Transaction
                {
                    NodeId = nodeId,
                    FromAddress = fromAddress,
                    ToAddress = _bcService.PenaltyStakingContractAddress,
                    Amount = amount,
                    Fee = fee,
                    Note = "PenaltyStake deposit"
                };

                tx.Signature = BlockChainService.SignPayload(tx.CanonicalPayload(), privateKey);

                _bcService.CreateTransaction(tx, nodeId);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpPost]
        public IActionResult PenaltyUnstake(string userAddress, decimal amount, string nodeId)
        {
            try
            {
                if (amount <= 0)
                    throw new Exception("Amount must be positive");

                var tx = new Transaction
                {
                    NodeId = nodeId,
                    FromAddress = _bcService.PenaltyStakingContractAddress,
                    ToAddress = userAddress,
                    Amount = amount,
                    Fee = 0,
                    Note = "PenaltyStake withdraw"
                };

                tx.Signature = BlockChainService.SignPayload(
                    tx.CanonicalPayload(),
                    _bcService.PrivateKeyXmlPenaltyStakingContract
                );

                _bcService.CreateTransaction(tx, nodeId);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction("Index", new { nodeId });
        }

        [HttpGet]
        public IActionResult GetPenaltyStakeInfo(string address, string nodeId = "A")
        {
            var chain = _bcService.GetChainAsync(nodeId).Result;
            var lastIndex = chain.Last().Index;

            if (!_bcService.Contracts.TryGetValue(_bcService.PenaltyStakingContractAddress, out var contract))
                return Json(new { });

            if (contract is not PenaltyStakingContract psc)
                return Json(new { });

            var (stake, reward, total, startBlock, blocksPassed) =
                psc.GetStakeInfo(address, lastIndex);

            return Json(new
            {
                stake,
                reward,
                total,
                startBlock,
                blocksPassed
            });
        }


    }
}
