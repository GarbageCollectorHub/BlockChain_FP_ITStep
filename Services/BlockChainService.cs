using BlockChain_FP_ITStep.Data;
using BlockChain_FP_ITStep.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace BlockChain_FP_ITStep.Services
{
    public class BlockChainService
    {
        private readonly ApplicationDbContext _db;

        private readonly RSAParameters _privateKey;
        private readonly RSAParameters _publicKey;
        private readonly string _publicKeyXml;


        public BlockChainService(ApplicationDbContext db)
        {
            _db = db;
           
             var rsa = RSA.Create();
            _privateKey = rsa.ExportParameters(true);
            _publicKey = rsa.ExportParameters(false);
            _publicKeyXml = rsa.ToXmlString(false);
            // ===


            InitGenBlock();
        }

        private void InitGenBlock()
        {
            if (!_db.Blocks.Any())
            {
                var genBlock = new Block(0, "Genesis-block", "");
                genBlock.Sign(_privateKey, _publicKeyXml);

                _db.Blocks.Add(genBlock);
                _db.SaveChanges();
            }
        }

        public async Task<List<Block>> GetAllBlocksAsync()
        {

            return await _db.Blocks.OrderBy(b => b.Index).ToListAsync();
        }

        public async Task AddBlockAsync(string data)
        {
            var blocks = await GetAllBlocksAsync();
            var prevBlock = blocks.LastOrDefault();

            if (prevBlock == null) return;

            var newBlock = new Block(blocks.Count, data, prevBlock.Hash);
            newBlock.Sign(_privateKey, _publicKeyXml);
            _db.Blocks.Add(newBlock);
            await _db.SaveChangesAsync();
        }

        public async Task<Block?> GetBlockByIndexAsync(int index)
        {
            return await _db.Blocks.FirstOrDefaultAsync(b => b.Index == index);
        }

        public async Task<bool> EditBlockAsync(int index, string data, string? signature = null)
        {
            var block = await GetBlockByIndexAsync(index);
            if (block == null) return false;

            block.Data = data;
            if (!string.IsNullOrWhiteSpace(signature))
            {
                block.UpdateSignature(signature);
            }          
            block.Hash = block.ComputeHash();

            _db.Blocks.Update(block);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> IsValidAsync()
        {
            var blocks = await GetAllBlocksAsync();

            for (int i = 1; i < blocks.Count; i++)
            {
                var current = blocks[i];
                var prevBlock = blocks[i - 1];

                if (current.PrevHash != prevBlock.Hash) return false;
                if (current.Hash != current.ComputeHash()) return false;
                if (!current.Verify(_publicKey)) return false;
            }
            return true;
        }

        public async Task<List<BlockValidationViewModel>> GetValidatedBlocksAsync()
        {
            var blocks = await GetAllBlocksAsync();
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
                        if (blocks[i].PrevHash != prev.Hash || !blocks[i].Verify(_publicKey))
                        {
                            stillValid = false;
                            isValid = false;
                        }
                    }
                    else
                    {
                        isValid = false; // всё после повреждённого блока — не валидно
                    }
                }

                result.Add(new BlockValidationViewModel
                {
                    Block = blocks[i],
                    IsValid = isValid
                });
            }

            return result;
        }


        //  === L2 ===

        



    }
}
