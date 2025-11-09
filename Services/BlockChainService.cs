using BlockChain_FP_ITStep.Data;
using BlockChain_FP_ITStep.Hubs;
using BlockChain_FP_ITStep.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BlockChain_FP_ITStep.Services
{
    public class BlockChainService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
        private readonly IHubContext<MiningHub> _hub;    // MiningHub (SignalR)
        public static int Difficulty { get; set; } = 3;
       
        public Dictionary<string, Wallet> Wallets {   get; set; } = new();
        public List<Transaction> Mempool { get; set; } = new();

        public const decimal MinerReward = 1.0m;

        // mining block difficulty adjustment
        private const int TargetBlockTimeSeconds = 4;   // Время за которое мы хотим чтобы в среднем добывался блок в секундах.
        private const int AdjustEveryBlocks = 5;         // Кол-во последних блоков, по которым будет оцениваться среднее время добычи блока, для достижении TargetBlockTimeSec
        private const double Tolerance = 0.2;            // На сколько может быть отклонение во времени (0.2 = 20%),  тоесть время добычи блоков в пределах отклонения в 20% - допустимо.

        private int maxDifficultyTest = 5;              // Ограничение сложности в диапазон, тестовое, TODO потом убрать?

        public BlockChainService(IDbContextFactory<ApplicationDbContext> dbFactory, IHubContext<MiningHub> hub)
        {
            _dbFactory = dbFactory;
            _hub = hub;

            using var db = _dbFactory.CreateDbContext();
            InitGenBlock(db);
            InitNodes(db);
        }

        private void InitGenBlock(ApplicationDbContext db)
        {
            if (db.Blocks.Any(b => b.NodeId == null))
                return;

            var genesis = new Block(
                index: 0,
                prevHash: "0",
                dateTime: new DateTime(2025, 01, 01, 00, 00, 00, DateTimeKind.Utc)
            );

            db.Blocks.Add(genesis);
            db.SaveChanges();
        }

        public Wallet RegisterWallet(string publicKeyXml, string displayName)
        {
            var wallet = new Wallet
            {
                PublicKeyXml = publicKeyXml,
                Address = Wallet.DereveAddressFromPublicKeyXml(publicKeyXml),
                DisplayName = displayName
            };
            Wallets[wallet.Address] = wallet;
            return wallet;
        }

        public void CreateTransaction(Transaction transaction, string nodeId)
        {
            transaction.NodeId = nodeId;

            var rsa = RSA.Create();
            var wallet = Wallets[transaction.FromAddress];

            rsa.FromXmlString(wallet.PublicKeyXml);
            var payload = Encoding.UTF8.GetBytes(transaction.CanonicalPayload());
            var sig = Convert.FromBase64String(transaction.Signature);

            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new Exception("Invalid Transaction Signature");

            var balances = GetBalances(nodeId, includeMempool: false).Result;               // include memorypool -> false 
            balances.TryGetValue(transaction.FromAddress, out var fromBalance);

            var required = transaction.Amount + transaction.Fee;
            if (fromBalance < required)
                throw new Exception("Insufficient funds");

            Mempool.Add(transaction); // общий список, но каждая транзакция помечена nodeId
        }

        public async Task<Block> MinePendingAsync(string privateKeyXml, string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();

            var prevBlock = await db.Blocks
                .Where(b => b.NodeId == nodeId)
                .OrderBy(b => b.Index)
                .LastOrDefaultAsync();


            if (prevBlock == null)
                throw new Exception($"Node {nodeId} has no genesis block!");
            //-----------

            using var rsa = RSA.Create();
            rsa.FromXmlString(privateKeyXml);
            var publicKeyXml = rsa.ToXmlString(false);

            var minerAddress = Wallets.Values.FirstOrDefault(w => w.PublicKeyXml == publicKeyXml)?.Address
                ?? throw new Exception("Miner wallet not found. Register wallet first using the PUBLIC key.");

            decimal totalFee = Mempool.Where(t => t.NodeId == nodeId).Sum(t => t.Fee);

            var txs = new List<Transaction>
            {
                new Transaction
                {
                    NodeId = nodeId,
                    FromAddress = "COINBASE",
                    ToAddress = minerAddress,
                    Amount = MinerReward + totalFee
                }
            };

            txs.AddRange(Mempool.Where(t => t.NodeId == nodeId));

            var newBlock = new Block((prevBlock?.Index ?? 0) + 1, prevBlock?.Hash ?? "0")
            {
                NodeId = nodeId
            };
            newBlock.SetTransaction(txs);
            newBlock.Mine(Difficulty);
            await AdjustDifficultyIfNeeded(nodeId);     // перерасчет сложночти для майнинга.  (по ноде или всем нодам? пока пусть будет по ноде)

            var privateParams = rsa.ExportParameters(true);
            newBlock.Sign(privateParams, publicKeyXml);

            foreach (var tx in txs) tx.Block = newBlock;

            db.Blocks.Add(newBlock);
            db.Transactions.AddRange(txs);
            await db.SaveChangesAsync();

            // очищаем только транзакции этой ноды
            Mempool.RemoveAll(t => t.NodeId == nodeId);

            return newBlock;
        }

        public async Task<long> AddBlockAsync(string data, string privateKeyXml, string nodeId)
        {
            try
            {
                var blocks = await GetAllBlocksAsync(nodeId);
                var prevBlock = blocks.LastOrDefault();
                if (prevBlock == null) return 0;

                using var db = _dbFactory.CreateDbContext();

                // Проверка что блок на этой позиции ещё не существует
                //var exists = await db.Blocks.AnyAsync(b => b.Index == blocks.Count);
                var exists = await db.Blocks.AnyAsync(b => b.NodeId == nodeId && b.Index == blocks.Count);
                if (exists)
                {
                    throw new InvalidOperationException("Block position conflict");
                }

                var newBlock = new Block(blocks.Count, prevBlock.Hash);

                // Mining
                newBlock.Mine(Difficulty);

                // Key validation
                var publicKeyXml = GetPublicKeyFromPrivate(privateKeyXml);
                if (string.IsNullOrEmpty(publicKeyXml))
                    throw new CryptographicException("Key format invalid");

                using var rsa = RSA.Create();
                rsa.FromXmlString(privateKeyXml);
                var privateParams = rsa.ExportParameters(true);

                newBlock.Sign(privateParams, publicKeyXml);

                // Save
                db.Blocks.Add(newBlock);
                await db.SaveChangesAsync();
                return newBlock.MiningDurationMs;
            }
            catch (CryptographicException)
            {
                throw new ApplicationException("Invalid private key. Please try again with a valid key.");
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("fork"))
            {
                throw new ApplicationException("Block rejected: chain fork detected. Refresh chain.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddBlockAsync] {ex.GetType().Name}: {ex.Message}");
                throw new ApplicationException("Unexpected error during block creation.");
            }
        }

        public async Task<List<Block>> GetAllBlocksAsync(string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Blocks
                .Where(b => b.NodeId == nodeId)
                .Include(b => b.Transactions)
                .OrderBy(b => b.Index)
                .ToListAsync();
        }

        public async Task<Block?> GetBlockByIndexAsync(int index)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Blocks.FirstOrDefaultAsync(b => b.Index == index);
        }

        public async Task<Block?> GetBlockByIdAsync(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Blocks.FirstOrDefaultAsync(b => b.Id == id);
        }

        public async Task<Block?> GetBlockByIdWithTransactionsAsync(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Blocks.Include(b => b.Transactions).FirstOrDefaultAsync(b => b.Id == id);
        }

        public async Task<bool> EditBlockAsync(int index, string? signature = null)
        {
            using var db = _dbFactory.CreateDbContext();

            var block = await db.Blocks.FirstOrDefaultAsync(b => b.Index == index);
            if (block == null) return false;

            if (!string.IsNullOrWhiteSpace(signature))
            {
                block.UpdateSignature(signature);
            }
            block.Hash = block.ComputeHash();

            db.Blocks.Update(block);
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> IsValidAsync(string nodeId)
        {
            var blocks = await GetAllBlocksAsync(nodeId);

            for (int i = 1; i < blocks.Count; i++)
            {
                var current = blocks[i];
                var prevBlock = blocks[i - 1];

                if (current.PrevHash != prevBlock.Hash) return false;
                if (current.Hash != current.ComputeHash()) return false;
                if (!current.Verify()) return false;
                if (!current.HashValidProof()) return false;
            }
            return true;
        }

        public async Task<List<BlockValidationViewModel>> GetValidatedBlocksAsync(string nodeId)
        {
            var blocks = await GetAllBlocksAsync(nodeId);
            var result = new List<BlockValidationViewModel>();
            bool stillValid = true;

            for (int i = 0; i < blocks.Count; i++)
            {
                bool isValid = true;
                if (i > 0)
                {
                    var prev = blocks[i - 1];
                    if (stillValid)
                    {
                        if (blocks[i].PrevHash != prev.Hash || !blocks[i].Verify())
                        {
                            stillValid = false;
                            isValid = false;
                        }
                    }
                    else isValid = false;    // всё после повреждённого блока — не валидно
                }

                result.Add(new BlockValidationViewModel { Block = blocks[i], IsValid = isValid });
            }
            return result;
        }

        public string GeneratePrivateKeyXml()
        {
            using var rsa = RSA.Create();
            return rsa.ToXmlString(true);      // true = экспортировать всю пару ключей(публичный + приватный компоненты)
        }

        public string? GetPublicKeyFromPrivate(string privateKeyXml)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.FromXmlString(privateKeyXml);   // может упасть, если не XML
                return rsa.ToXmlString(false);      // fase  экспортируем только открытую часть
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetPublicKeyFromPrivate] Invalid key: {ex.Message}");
                return null;
            }
        }


        // Валидация сигнатуры для valid/invalid signature в Index()
        public async Task<List<BlockValidationViewModel>> GetSignatureValidationAsync(string nodeId)
        {
            var blocks = await GetAllBlocksAsync(nodeId);

            return blocks.Select(b => new BlockValidationViewModel
            {
                Block = b,
                IsValid = b.Index == 0 ? true : b.Verify()
            }).ToList();
        }


        //=============================================// 
        //  Старый Асинк метод - уже не нужен?  Но! тут остался СигналР
        public async Task<long> MineAsync(string privateKeyXml, string nodeId, CancellationToken ct, IProgress<int>? progress = null)
        {
            var blocks = await GetAllBlocksAsync(nodeId);         // получаем текущую цепочку
            var prevBlock = blocks.Last();
            var newBlock = new Block(blocks.Count, prevBlock.Hash)
            {
                Difficulty = Difficulty
            };

            string target = new string('0', Difficulty);    // строка вида "000"
            var sw = Stopwatch.StartNew();
            int tries = 0;

            // attempts/sec (перебор Nonce)
            long attemptCounter = 0;
            var rateTimer = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                newBlock.Nonce++;
                newBlock.Hash = newBlock.ComputeHash();
                tries++;
                attemptCounter++;

                // Обновляем прогресс каждые 5000 попыток (чтобы не спамить signalr)
                if (tries % 5000 == 0)
                {
                    int percent = Math.Min(99, tries / 20000);
                    progress?.Report(percent);
                    await _hub.Clients.All.SendAsync("MiningProgress", percent);
                }

                // отправка attempts/sec раз в секунду
                if (rateTimer.ElapsedMilliseconds >= 1000)
                {
                    await _hub.Clients.All.SendAsync("MiningAttemptsPerSecond", attemptCounter);
                    attemptCounter = 0;
                    rateTimer.Restart();
                }

                // нужный хэш найден
                if (newBlock.Hash.StartsWith(target))
                {
                    sw.Stop();
                    newBlock.MiningDurationMs = sw.ElapsedMilliseconds;

                    // подписываем блок
                    using var rsa = RSA.Create();
                    rsa.FromXmlString(privateKeyXml);
                    var privateParams = rsa.ExportParameters(true);
                    var publicKeyXml = rsa.ToXmlString(false);
                    newBlock.Sign(privateParams, publicKeyXml);

                    using var db = _dbFactory.CreateDbContext();
                    db.Blocks.Add(newBlock);
                    await db.SaveChangesAsync();

                    await _hub.Clients.All.SendAsync("MiningProgress", 100);
                    await _hub.Clients.All.SendAsync("MiningAttemptsPerSecond", 0);

                    return newBlock.MiningDurationMs;
                }
            }

            // Если майнинг остановлен
            await _hub.Clients.All.SendAsync("MiningProgress", -1);
            await _hub.Clients.All.SendAsync("MiningAttemptsPerSecond", 0);
            return -1;
        }

        //=============================================// 



        // Demo Method, later be remooved...but now using in Demo Setup (dmeo btn)
        public (Wallet wallet, string privateKeyXml) CreateWallet(string displayName)
        {
            var rsa = RSA.Create();
            var privateKeyXml = rsa.ToXmlString(true);
            var publicKeyXml = rsa.ToXmlString(false);
            var wallet = RegisterWallet(publicKeyXml, displayName);
            return (wallet, privateKeyXml);
        }

        public static string SignPayload(string payload, string privateKeyXml)
        {
            var rsa = RSA.Create();
            rsa.FromXmlString(privateKeyXml);
            var data = Encoding.UTF8.GetBytes(payload);
            var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(sig);
        }

        public async Task<Dictionary<string, decimal>> GetBalances(string nodeId, bool includeMempool = false)
        {
            using var db = _dbFactory.CreateDbContext();

            var blocks = await db.Blocks
                .Where(b => b.NodeId == nodeId)
                .Include(b => b.Transactions)
                .OrderBy(b => b.Index)
                .ToListAsync();

            var balances = new Dictionary<string, decimal>();

            foreach (var block in blocks)
                foreach (var tx in block.Transactions)
                    ApplyTransactionToBalances(balances, tx);

            if (includeMempool)
            {
                // фильтруем общий Mempool по nodeId
                foreach (var tx in Mempool.Where(t => t.NodeId == nodeId))
                    ApplyTransactionToBalances(balances, tx);
            }
            return balances;
        }

        private static void ApplyTransactionToBalances(Dictionary<string, decimal> balances, Transaction tx)
        {
            if(!balances.TryGetValue(tx.ToAddress, out var toBal))
            {
                toBal = 0;
            }
            balances[tx.ToAddress] = toBal + tx.Amount;

            if(tx.FromAddress != "COINBASE")
            {
                if(!balances.TryGetValue(tx.FromAddress, out var fromBal))
                    fromBal = 0;
                balances[tx.FromAddress] = fromBal - (tx.Amount + tx.Fee);
            }
        }

        // ===  Nodes les  ===

        public async Task<bool> TryAddExternalChainAsync(List<Block> incoming, string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();

            var current = await GetChainAsync(nodeId);
            if (incoming.Count <= current.Count)
                return false;

            // Проверка целостности входящей цепочки
            for (int i = 0; i < incoming.Count; i++)
            {
                var cur = incoming[i];

                // Пропускаем проверку GENESIS блока (index = 0)
                if (cur.Index == 0)
                    continue;

                var prev = incoming[i - 1];

                if (cur.PrevHash != prev.Hash) return false;
                if (cur.Hash != cur.ComputeHash()) return false;

                // Верификация подписи для НЕ genesis блока
                if (!string.IsNullOrEmpty(cur.Signature))
                {
                    if (!cur.Verify())
                        return false;
                }

                // Проверка POW только если есть nonce (генезис без POW)
                if (!cur.HashValidProof()) return false;
            }

            // Сначала удаляем транзакции этой ноды
            db.Transactions.RemoveRange(
                db.Transactions.Where(t => t.NodeId == nodeId)
            );
            await db.SaveChangesAsync();

            // Теперь удаляем блоки этой ноды
            db.Blocks.RemoveRange(
                db.Blocks.Where(b => b.NodeId == nodeId)
            );
            await db.SaveChangesAsync();

            // Вставляем новую цепочку (глубокое копирование данных)
            foreach (var s in incoming)
            {
                Block clone;

                // Для genesis используем конструктор с DateTime, БЕЗ подписи
                if (s.Index == 0)
                {
                    clone = new Block(s.Index, s.PrevHash, s.Timestamp)
                    {
                        Hash = s.Hash,
                        NodeId = nodeId,
                        MiningDurationMs = s.MiningDurationMs,
                        Nonce = s.Nonce,
                        Difficulty = s.Difficulty
                    };
                }
                else
                {
                    // Обычные блоки
                    clone = new Block(s.Index, s.PrevHash)
                    {
                        Timestamp = s.Timestamp,
                        Hash = s.Hash,
                        NodeId = nodeId,
                        MiningDurationMs = s.MiningDurationMs,
                        Nonce = s.Nonce,
                        Difficulty = s.Difficulty
                    };

                    clone.UpdatePublicKey(s.PublicKeyXml);
                    clone.UpdateSignature(s.Signature);
                }

                foreach (var t in s.Transactions)
                {
                    var nt = new Transaction
                    {
                        NodeId = nodeId,
                        FromAddress = t.FromAddress,
                        ToAddress = t.ToAddress,
                        Amount = t.Amount,
                        Fee = t.Fee,
                        Note = t.Note
                    };

                    clone.Transactions.Add(nt);
                }

                db.Blocks.Add(clone);
            }

            await db.SaveChangesAsync();
            return true;
        }


        public async Task BroadcastChainAsync(string sourceNodeId)
        {
            var fullChain = await GetChainAsync(sourceNodeId);
            var nodes = await GetNodeIdsAsync();

            foreach (var nodeId in nodes)
            {
                if (nodeId == sourceNodeId) continue;
                await TryAddExternalChainAsync(fullChain, nodeId);
            }
        }

        // Получение цепочки ноды с БД по nodeId
        public async Task<List<Block>> GetChainAsync(string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Blocks
                .Where(b => b.NodeId == nodeId)
                .Include(b => b.Transactions)
                .OrderBy(b => b.Index)
                .ToListAsync();
        }

        // список уникальных nodeId из БД
        public async Task<List<string>> GetNodeIdsAsync()
        {
            using var db = _dbFactory.CreateDbContext();

            return await db.Blocks
                .Where(b => b.NodeId != null)  // фильтруем NULL genesis
                .Select(b => b.NodeId!)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();
        }

        private void InitNodes(ApplicationDbContext db)
        {
            var nodeIds = new[] { "A", "B", "C" };

            // есть ли генезис у сети
            var globalGenesis = db.Blocks.FirstOrDefault(b => b.NodeId == null);
            if (globalGenesis == null)
                return;

            foreach (var id in nodeIds)
            {
                // есть ли у ноды генезис
                bool exists = db.Blocks.Any(b => b.NodeId == id && b.Index == 0);
                if (exists)
                    continue;

                var nodeGenesis = new Block(index: 0, prevHash: "0", dateTime: new DateTime(2025, 01, 01, 00, 00, 00, DateTimeKind.Utc))
                {
                    NodeId = id,
                    Difficulty = globalGenesis.Difficulty,
                    MiningDurationMs = globalGenesis.MiningDurationMs,
                    Nonce = globalGenesis.Nonce,
                    Hash = globalGenesis.Hash
                };

                db.Blocks.Add(nodeGenesis);
            }

            db.SaveChanges();
        }


        //private void AdjustDifficultyIfNeeded()
        //{
        //    if (Chain.Count % AdjustEveryBlocks != 0 || Chain.Count < AdjustEveryBlocks)
        //    {
        //        return;
        //    }

        //    var recnt = Chain.Skip(1).TakeLast(AdjustEveryBlocks).ToList();

        //    if (recnt.Count < AdjustEveryBlocks)
        //    {
        //        return;
        //    }

        //    var avgMs = recnt.Average(b => b.MiningDurationMs);
        //    var targetMs = TargetBlockTimeSeconds * 1000;           // переводим ms в seconds

        //    var lowerBound = targetMs * (1 - Tolerance);
        //    var upperBound = targetMs * (1 + Tolerance);

        //    if (avgMs < lowerBound)
        //    {
        //        Difficulty++;
        //    }
        //    else if (avgMs > upperBound && Difficulty > 1)
        //    {
        //        Difficulty--;
        //    }

        //    if (Difficulty < 1) Difficulty = 1;
        //    if (Difficulty > 10) Difficulty = 10;
        //}


        // Перерасчёт сложности майнинга для конкретной ноды.
        // После каждого блока (начиная с 5-го) считаем среднее время добычи последних N блоков
        // и увеличиваем/уменьшаем сложность, чтобы удерживать среднее время около TargetBlockTimeSeconds.
        private async Task AdjustDifficultyIfNeeded(string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();

            // Сколько блоков всего у ноды
            int totalCount = await db.Blocks.CountAsync(b => b.NodeId == nodeId);

            // Пока меньше чем AdjustEveryBlocks — не пересчитываем
            if (totalCount < AdjustEveryBlocks)
                return;

            // Берём последние N блоков, исключая genesis (Index > 0)
            var recentBlocks = await db.Blocks
                .Where(b => b.NodeId == nodeId && b.Index > 0)
                .OrderByDescending(b => b.Index)
                .Take(AdjustEveryBlocks)
                .ToListAsync();

            if (recentBlocks.Count < AdjustEveryBlocks)
                return;

            var avgMs = recentBlocks.Average(b => b.MiningDurationMs);
            var targetMs = TargetBlockTimeSeconds * 1000; // target в ms

            var lowerBound = targetMs * (1 - Tolerance);
            var upperBound = targetMs * (1 + Tolerance);

            // Если блоки добывались быстрее нормы — увеличиваем сложность
            if (avgMs < lowerBound)
                Difficulty++;
            // Если добывались медленнее нормы — уменьшаем
            else if (avgMs > upperBound)
                Difficulty--;

            
            if (Difficulty < 1) Difficulty = 1;
            if (Difficulty > maxDifficultyTest) Difficulty = maxDifficultyTest;         // Ограничение сложности в диапазон, тестовое, TODO потом убрать?
        }



    }
}
