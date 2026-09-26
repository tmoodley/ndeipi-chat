// Deploys NdeipiPosts. ADMIN is who can grant roles and repoint metadata (a multisig is best);
// MINTER is Ndeipi Enterprise Server's minting wallet.
//   ADMIN=0x… MINTER=0x… DEPLOYER_PRIVATE_KEY=0x… npm run deploy:amoy
const { ethers, network } = require("hardhat");

async function main() {
  const { ADMIN, MINTER } = process.env;
  if (!ethers.isAddress(ADMIN ?? "") || !ethers.isAddress(MINTER ?? ""))
    throw new Error("Set ADMIN and MINTER to the admin and minter wallet addresses.");

  const posts = await ethers.deployContract("NdeipiPosts", [ADMIN, MINTER]);
  await posts.waitForDeployment();
  const address = await posts.getAddress();

  console.log(`NdeipiPosts deployed to ${network.name} at ${address}`);
  console.log("Set in the API's appsettings:");
  console.log(JSON.stringify({ Social: { Nft: { Chain: network.name === "amoy" ? "amoy" : "polygon", ContractAddress: address } } }, null, 2));
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
