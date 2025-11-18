using BlockChain_FP_ITStep.Models.Contracts.Interfaces;
using BlockChain_FP_ITStep.Services;

namespace BlockChain_FP_ITStep.Models.Contracts
{
    public class PenaltyStakingContract : ISmartContract
    {
        public string Address { get; }

        private readonly decimal _rewardPerBlockPerToken;
        private readonly int _minLockBlocks;
        private readonly decimal _earlyPenaltyPercent;

        // state
        private readonly Dictionary<string, decimal> _stakes = new();     // сколько застейкано
        private readonly Dictionary<string, int> _startBlock = new();     // блок, когда начался стейк

        public PenaltyStakingContract(string address, decimal rewardPerBlockPerToken, int minLockBlocks, decimal earlyPenaltyPercent)
        {
            Address = address;
            _rewardPerBlockPerToken = rewardPerBlockPerToken;
            _minLockBlocks = minLockBlocks;
            _earlyPenaltyPercent = earlyPenaltyPercent;
        }


        public bool ValidateTransaction(BlockChainService chain, Transaction tx, int currentBlock)
        {
            bool isDeposit = tx.ToAddress.Equals(Address, StringComparison.OrdinalIgnoreCase);
            bool isWithdraw = tx.FromAddress.Equals(Address, StringComparison.OrdinalIgnoreCase);

            if (!isDeposit && !isWithdraw)
                return true; // не наш контракт


            // STAKE

            if (isDeposit)
            {
                var user = tx.FromAddress;

                if (!_stakes.TryGetValue(user, out var stake))
                    stake = 0;

                _stakes[user] = stake + tx.Amount;

                if (!_startBlock.ContainsKey(user))
                    _startBlock[user] = currentBlock;

                return true;
            }

            // UNSTAKE

            if (isWithdraw)
            {
                var user = tx.ToAddress;

                if (!_stakes.TryGetValue(user, out var stake))
                    return false; // нечего выводить

                if (!_startBlock.TryGetValue(user, out var startBlock))
                    return false;

                int heldBlocks = currentBlock - startBlock;

                // calculate reward
                decimal reward = stake * _rewardPerBlockPerToken * heldBlocks;
                decimal maxAllowed;

                if (heldBlocks >= _minLockBlocks)
                {
                    // без штрафа
                    maxAllowed = stake + reward;
                }
                else
                {
                    // досрочно -> штраф
                    decimal penalty = stake * _earlyPenaltyPercent;
                    maxAllowed = stake - penalty;  // reward не начисляется
                }

                // если юзер запрашивает больше
                if (tx.Amount > maxAllowed)
                    return false;

                // успешный вывод -> очищаем стейк
                _stakes[user] = 0;
                _startBlock.Remove(user);

                return true;
            }

            return true;
        }

        public (decimal stake, decimal reward, decimal total, int startBlock, int blocksPassed) GetStakeInfo(string userAddress, int currentBlock)
        {
            if (!_stakes.TryGetValue(userAddress, out var stake))
                return (0m, 0m, 0m, 0, 0);

            if (!_startBlock.TryGetValue(userAddress, out var startBlock))
                return (0m, 0m, 0m, 0, 0);

            int blocksPassed = currentBlock - startBlock;
            if (blocksPassed < 0) blocksPassed = 0;

            // reward = stake * rate * blocks
            decimal reward = stake * _rewardPerBlockPerToken * blocksPassed;

            decimal total =
                blocksPassed < _minLockBlocks
                ? stake * (1 - _earlyPenaltyPercent)        // штраф
                : stake + reward;                           // нормальный вывод

            return (stake, reward, total, startBlock, blocksPassed);
        }



    }
}
