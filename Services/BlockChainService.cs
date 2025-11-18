using BlockChain_FP_ITStep.Data;
using BlockChain_FP_ITStep.Hubs;
using BlockChain_FP_ITStep.Models;
using BlockChain_FP_ITStep.Models.Contracts;
using BlockChain_FP_ITStep.Models.Contracts.Interfaces;
using BlockChain_FP_ITStep.Models.ViewModel;
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
        private readonly IHubContext<MiningHub> _hub;       // MiningHub (SignalR)


        public Dictionary<string, Wallet> Wallets {   get; set; } = new();
        public List<Transaction> Mempool { get; set; } = new(); 

        // Smart Contract collection
        public Dictionary<string, ISmartContract> Contracts { get;} = new Dictionary<string, ISmartContract>(StringComparer.OrdinalIgnoreCase);

        // === Staking Smart-contract (temp states) ===
        // TODO: модель для контрактов -> напр. Contractwallet -> str Adress, str PrivKey, str PubKey.  // и + контейнер для них?
        public string StakingContractAddress { get; private set; }
        public  string PrivateKeyXmlStakingContract {  get; private set; }
        public string PublicKeyXmlStakingContract { get; private set; }


        // === Penalty Staking Contract (temp states) ===  
        public string PenaltyStakingContractAddress { get; private set; }
        public string PrivateKeyXmlPenaltyStakingContract { get; private set; }
        public string PublicKeyXmlPenaltyStakingContract { get; private set; }



        // Сложность для PoW алгоритма.
        public static int Difficulty { get; set; } = 1;     
        // Halving
        private const decimal BaseMinerReward = 50.0m;      // Base block reward
        private const int HalvingBlockInterval = 10;        // Reward halves every N blocks

        // mining block difficulty adjustment
        private const int TargetBlockTimeSeconds = 5;       // Время за которое мы хотим чтобы в среднем добывался блок в секундах.
        private const int AdjustEveryBlocks = 5;            // Кол-во последних блоков, по которым будет оцениваться среднее время добычи блока, для достижении TargetBlockTimeSec
        private const double Tolerance = 0.2;               // На сколько может быть отклонение во времени (0.2 = 20%),  тоесть время добычи блоков в пределах отклонения в 20% - допустимо.

        private int maxDifficultyTest = 3;                  // Ограничение сложности в диапазон, тестовое, TODO потом убрать?




        public BlockChainService(IDbContextFactory<ApplicationDbContext> dbFactory, IHubContext<MiningHub> hub)
        {
            _dbFactory = dbFactory;
            _hub = hub;

            using var db = _dbFactory.CreateDbContext();
            InitGenBlock(db);
            InitNodes(db);

            //temporary test conctract initialization
            // TODO: временная инициализация тестового смарт-контракта TimeLock

            var contractPrivateKeyXml = GeneratePrivateKeyXml();
            var contractPublicKeyXml = GetPublicKeyFromPrivate(contractPrivateKeyXml)!;

            var timeLockContractAddress = RegisterWallet(contractPublicKeyXml, "Test TimeLockContract").Address;
            Contracts[timeLockContractAddress] = new TimeLockContract(timeLockContractAddress, 50);     // 50 - block index to unlock contract transaction

            // === Staking-Contract init ===
            var rsaStakingContract = RSA.Create();

            PrivateKeyXmlStakingContract = rsaStakingContract.ToXmlString(true);
            PublicKeyXmlStakingContract  = rsaStakingContract.ToXmlString(false);

            var stakingWallet = RegisterWallet(PublicKeyXmlStakingContract, "Staking Contract Wallet");
            StakingContractAddress = stakingWallet.Address;

            decimal rewardPerBlock = 0.001m;        // 0.001 -> reward per staked coin for each block 
            int lockPeriod = 20;                    // 20 -> number of blocks during which coins remain locked
            Contracts[StakingContractAddress] = new StakingContract(StakingContractAddress, rewardPerBlock, lockPeriod);

            // === Penalty Staking Contract ===
            var penaltyRsa = RSA.Create();

            PrivateKeyXmlPenaltyStakingContract = penaltyRsa.ToXmlString(true);
            PublicKeyXmlPenaltyStakingContract = penaltyRsa.ToXmlString(false);

            var penaltyWallet = RegisterWallet(PublicKeyXmlPenaltyStakingContract, "PenaltyStaking Contract");
            PenaltyStakingContractAddress = penaltyWallet.Address;

            Contracts[PenaltyStakingContractAddress] = new PenaltyStakingContract(PenaltyStakingContractAddress, rewardPerBlockPerToken: 0.001m, minLockBlocks: 20, earlyPenaltyPercent: 0.20m);   //  0.20m ->  pentlty 20%


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

            if (transaction.Amount < 0 || transaction.Fee < 0)
                throw new Exception("Amount and Fee must be non-negative");

            // является ли отправитель контрактом или COINBASE
            bool isFromContract = Contracts.ContainsKey(transaction.FromAddress);
            bool isCoinbase = string.Equals(transaction.FromAddress, "COINBASE", StringComparison.OrdinalIgnoreCase);

            // теперь баланс проверяется только для обычных кошельков
            if (!isFromContract && !isCoinbase)
            {
                var balances = GetBalances(nodeId, includeMempool: false).Result;               // include memorypool -> false 
                balances.TryGetValue(transaction.FromAddress, out var fromBalance);

                var required = transaction.Amount + transaction.Fee;
                if (fromBalance < required)
                    throw new Exception("Insufficient funds");
            }


            // --- Temporary Smart-contract validation ---
            // TODO rework DB get -> nextBlockIndex -> every CreateTransaction()?

            int nextBlockIndex;
            using (var db = _dbFactory.CreateDbContext())
            {
                var maxIndex = db.Blocks
                    .Where(b => b.NodeId == nodeId)
                    .Select(b => (int?)b.Index)
                    .Max() ?? 0;

                nextBlockIndex = maxIndex + 1;
            }

            if (Contracts.TryGetValue(transaction.FromAddress, out var contractFrom))
            {
                // контракт-отправитель (например, staking withdraw) может отклонить или модифицировать транзакцию
                var ok = contractFrom.ValidateTransaction(this, transaction, nextBlockIndex);
                if (!ok) return;
            }

            if (Contracts.TryGetValue(transaction.ToAddress, out var contractTo))
            {
                // контракт-получатель (deposit) тоже решает, принимать операцию или нет
                var ok = contractTo.ValidateTransaction(this, transaction, nextBlockIndex);
                if (!ok) return;
            }
            // ----------------------------------------- //


            Mempool.Add(transaction); // общий список, но каждая транзакция помечена nodeId
        }

        public (decimal stake, decimal reward, decimal total) GetStakeSummary(string userAddress, int currentBlock)
        {
            if (!Contracts.TryGetValue(StakingContractAddress, out var contract))
                return (0, 0, 0);

            if (contract is not StakingContract stakeContract)
                return (0, 0, 0);

            var (stake, reward) = stakeContract.GetStakeInfo(userAddress, currentBlock);
            return (stake, reward, stake + reward);
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

            var newBlock = new Block((prevBlock?.Index ?? 0) + 1, prevBlock?.Hash ?? "0")
            {
                NodeId = nodeId
            };

            // Miner reward + halving logic (reward decreases every N blocks)
            var minerReward = GetCurrentBlockReward(newBlock.Index);

            var txs = new List<Transaction>
            {
                new Transaction
                {
                    NodeId = nodeId,
                    FromAddress = "COINBASE",
                    ToAddress = minerAddress,
                    Amount = minerReward + totalFee
                }
            };

            txs.AddRange(Mempool.Where(t => t.NodeId == nodeId));

            newBlock.SetTransaction(txs);
            newBlock.Mine(Difficulty);
            await AdjustDifficultyIfNeeded(nodeId);     // перерасчет сложночти для майнинга.  (по ноде или всем нодам? пока пусть будет по ноде)

            var privateParams = rsa.ExportParameters(true);
            newBlock.Sign(privateParams, publicKeyXml);

            foreach (var tx in txs) tx.Block = newBlock;

            db.Blocks.Add(newBlock);
            db.Transactions.AddRange(txs);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate") == true)
            {
                throw new Exception("Block was not added - chain already extended by another block.");      // Блок отклонён - другой блок уже стоит на этом месте цепи. Мы не позволяем переписывать уже подтверждённые блоки
            }

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

        public async Task<bool> TryAddExternalChainAsync(List<Block> incoming, string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();
            // Текущая цепочка ноды
            var current = await GetChainAsync(nodeId);

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

            // принимаем входящую цепь только если она содержит больше работы (PoW консенсус)
            var currentWork = ComputeTotalWork(current);
            var incomingWork = ComputeTotalWork(incoming);
            if (incomingWork <= currentWork)
                return false;


            // удаляем транзакции этой ноды
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

        // return transactions by wallet (address)
        public async Task<List<WalletTransactionViewModel>> GetTransactionsByWalletAsync(string address, string nodeId)
        {
            using var db = _dbFactory.CreateDbContext();

            // Транзакции из блокчейна
            var chainTx = await db.Blocks
                .Where(b => b.NodeId == nodeId)
                .Include(b => b.Transactions)
                .OrderBy(b => b.Index)
                .SelectMany(b => b.Transactions.Select(t => new WalletTransactionViewModel
                {
                    Tx = t,
                    BlockIndex = b.Index
                }))
                .Where(x => x.Tx.FromAddress == address || x.Tx.ToAddress == address)
                .ToListAsync();

            // Транзакции в мемпуле (pending)
            var memTx = Mempool
                .Where(t => t.FromAddress == address || t.ToAddress == address)
                .Select(t => new WalletTransactionViewModel
                {
                    Tx = t,
                    BlockIndex = null // pending
                })
                .ToList();

            // Объединяем
            return chainTx
                .Concat(memTx)
                .OrderByDescending(x => x.BlockIndex ?? int.MaxValue) // pending вверху
                .ThenByDescending(x => x.Tx.Id) // среди pending сортируем по Id
                .ToList();
        }

        // Returns halved mining reward based on block index (reward halves every HalvingBlockInterval blocks)
        public decimal GetCurrentBlockReward(int newBlockIndex)
        {
            if (newBlockIndex < 1) return 0;

            int halvings = (newBlockIndex / HalvingBlockInterval);
            decimal reward = BaseMinerReward;
            for (int i = 0; i < halvings; i++)
            {
                reward /= 2;
            }
            return reward;
        }

        public decimal GetBlockReward(int blockIndex)
        {
            return GetCurrentBlockReward(blockIndex);
        }

        // Computes total Proof-of-Work of a chain. 
        // Используем при сравнении цепей - принимаем только цепь с большей суммарной работой...
        private static double ComputeTotalWork(List<Block> chain)
        {
            double totalWork = 0;

            foreach (var block in chain)
                totalWork += Math.Pow(2, block.Difficulty);

            return totalWork;
        }
    }
}
