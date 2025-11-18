using BlockChain_FP_ITStep.Services;

namespace BlockChain_FP_ITStep.Models.Contracts.Interfaces
{
    public interface ISmartContract
    {
        string Address { get; }

        bool ValidateTransaction(BlockChainService chain, Transaction tx, int currentBlock);
    }
}
