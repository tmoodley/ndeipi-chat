const { expect } = require("chai");
const { ethers } = require("hardhat");

describe("NdeipiPosts", () => {
  // What the API puts on the queue for a post: its id as a 128-bit number, and its metadata URL.
  const tokenId = BigInt("0x" + "3f2504e04f8911d39a0c0305e82c3301");
  const uri = "https://chat.ndeipi.com/nft/posts/3f2504e04f8911d39a0c0305e82c3301";

  async function deploy() {
    const [admin, minter, author, stranger] = await ethers.getSigners();
    const posts = await ethers.deployContract("NdeipiPosts", [admin.address, minter.address]);
    return { posts, admin, minter, author, stranger };
  }

  it("is an ERC-721 named Ndeipi Posts", async () => {
    const { posts } = await deploy();
    expect(await posts.name()).to.equal("Ndeipi Posts");
    expect(await posts.symbol()).to.equal("NDPOST");
    expect(await posts.supportsInterface("0x80ac58cd")).to.equal(true); // ERC-721
    expect(await posts.supportsInterface("0x5b5e139f")).to.equal(true); // ERC-721 metadata
    expect(await posts.supportsInterface("0x49064906")).to.equal(true); // ERC-4906 metadata updates
  });

  it("mints a post to its author with the queue's token id and metadata URL", async () => {
    const { posts, minter, author } = await deploy();
    expect(await posts.exists(tokenId)).to.equal(false);

    await expect(posts.connect(minter).mint(author.address, tokenId, uri))
      .to.emit(posts, "Transfer").withArgs(ethers.ZeroAddress, author.address, tokenId);

    expect(await posts.ownerOf(tokenId)).to.equal(author.address);
    expect(await posts.tokenURI(tokenId)).to.equal(uri);
    expect(await posts.exists(tokenId)).to.equal(true);
  });

  it("never mints the same post twice, so a retried mint can't duplicate it", async () => {
    const { posts, minter, author } = await deploy();
    await posts.connect(minter).mint(author.address, tokenId, uri);

    await expect(posts.connect(minter).mint(author.address, tokenId, uri))
      .to.be.revertedWithCustomError(posts, "ERC721InvalidSender");
  });

  it("only lets the minter mint", async () => {
    const { posts, author, stranger } = await deploy();
    await expect(posts.connect(stranger).mint(author.address, tokenId, uri))
      .to.be.revertedWithCustomError(posts, "AccessControlUnauthorizedAccount");
  });

  it("lets the admin, and only the admin, repoint a token's metadata", async () => {
    const { posts, admin, minter, author } = await deploy();
    await posts.connect(minter).mint(author.address, tokenId, uri);
    const moved = uri.replace("chat.ndeipi.com", "ndeipi.example");

    await expect(posts.connect(minter).setTokenURI(tokenId, moved))
      .to.be.revertedWithCustomError(posts, "AccessControlUnauthorizedAccount");
    await expect(posts.connect(admin).setTokenURI(tokenId, moved))
      .to.emit(posts, "MetadataUpdate").withArgs(tokenId);
    expect(await posts.tokenURI(tokenId)).to.equal(moved);
    await expect(posts.connect(admin).setTokenURI(tokenId + 1n, moved))
      .to.be.revertedWithCustomError(posts, "ERC721NonexistentToken");
  });

  it("belongs to its author once minted: they can send it on, e.g. from a chat", async () => {
    const { posts, minter, author, stranger } = await deploy();
    await posts.connect(minter).mint(author.address, tokenId, uri);

    await posts.connect(author).transferFrom(author.address, stranger.address, tokenId);
    expect(await posts.ownerOf(tokenId)).to.equal(stranger.address);
  });

  it("can hand minting to a new wallet", async () => {
    const { posts, admin, minter, author, stranger } = await deploy();
    const role = await posts.MINTER_ROLE();
    await posts.connect(admin).grantRole(role, stranger.address);
    await posts.connect(admin).revokeRole(role, minter.address);

    await expect(posts.connect(minter).mint(author.address, tokenId, uri))
      .to.be.revertedWithCustomError(posts, "AccessControlUnauthorizedAccount");
    await posts.connect(stranger).mint(author.address, tokenId, uri);
    expect(await posts.ownerOf(tokenId)).to.equal(author.address);
  });
});
