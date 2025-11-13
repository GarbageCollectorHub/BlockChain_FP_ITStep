using BlockChain_FP_ITStep.Services;

namespace BlockChain_FP_ITStep.Models.Contracts
{
    public class TimeLockContract : ISmartContract
    {
        public string Address { get; }
        public int UnlockBlockIndex { get; set; }

        public TimeLockContract(string address, int unlockBlockIndex)
        {
            Address = address;
            UnlockBlockIndex = unlockBlockIndex;
        }

        public bool ValidateTransaction(BlockChainService chain, Transaction tx, int currentBlock)
        {
            if (String.Equals(Address, tx.FromAddress, StringComparison.OrdinalIgnoreCase))
            {
                if (currentBlock < UnlockBlockIndex)
                {
                    return false;
                    // throw new Exception($"TimeLockContract: Transaction from address {Address} is locked until block {UnlockBlockIndex}. Current block is {currentBlock}.");
                }
                return true;
            }
            return false;
        }
    }

}
