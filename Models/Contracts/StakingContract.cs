using BlockChain_FP_ITStep.Models.Contracts.Interfaces;
using BlockChain_FP_ITStep.Services;

namespace BlockChain_FP_ITStep.Models.Contracts
{
    public class StakingContract : ISmartContract
    {
        public string Address { get; }

        private readonly decimal _rewardPerBlock;
        private readonly int _lockPeriod;

        private readonly Dictionary<string, decimal> _stakes = new();
        private readonly Dictionary<string, int> _stakeStartBlock = new();

        public StakingContract(string address, decimal rewardPerBlock, int lockPeriodBlocks)
        {
            Address = address;
            _rewardPerBlock = rewardPerBlock;
            _lockPeriod = lockPeriodBlocks;
        }

        public bool ValidateTransaction(BlockChainService chain, Transaction tx, int currentBlock)
        {
            bool isDeposit = string.Equals(tx.ToAddress, Address, StringComparison.OrdinalIgnoreCase);
            bool isWithdraw = string.Equals(tx.FromAddress, Address, StringComparison.OrdinalIgnoreCase);

            if (isDeposit)
                return HandleDeposit(tx, currentBlock);

            if (isWithdraw)
                return HandleWithdraw(tx, currentBlock);

            return false;
        }

        private bool HandleDeposit(Transaction tx, int currentBlock)
        {
            var user = tx.FromAddress;

            if (!_stakes.TryGetValue(user, out var stake))
                stake = 0m;

            _stakes[user] = stake + tx.Amount;

            if (!_stakeStartBlock.ContainsKey(user))
                _stakeStartBlock[user] = currentBlock;

            return true;
        }

        private bool HandleWithdraw(Transaction tx, int currentBlock)
        {
            var user = tx.ToAddress;

            if (!_stakes.TryGetValue(user, out var stake))
                return false;

            if (!_stakeStartBlock.TryGetValue(user, out var startBlock))
                return false;

            if (currentBlock < startBlock + _lockPeriod)
                return false;

            decimal reward = (currentBlock - startBlock) * _rewardPerBlock * stake;
            decimal totalPayout = stake + reward;

            if (tx.Amount > totalPayout)
                return false;

            tx.Amount = totalPayout;

            _stakes[user] = 0m;
            _stakeStartBlock.Remove(user);

            return true;
        }

        public (decimal stake, decimal reward) GetStakeInfo(string userAddress, int currentBlock)
        {
            decimal currentStake = 0m;
            int startBlock = 0;

            if (_stakes.TryGetValue(userAddress, out var s))
                currentStake = s;

            if (_stakeStartBlock.TryGetValue(userAddress, out var b))
                startBlock = b;

            if (currentStake <= 0 || startBlock <= 0)
                return (0m, 0m);

            decimal reward = (currentBlock - startBlock) * _rewardPerBlock * currentStake;

            return (currentStake, reward);
        }
    }
}
