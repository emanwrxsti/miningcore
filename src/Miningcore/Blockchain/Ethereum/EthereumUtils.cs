namespace Miningcore.Blockchain.Ethereum;

public static class EthereumUtils
{
    public static void DetectNetworkAndChain(string netVersionResponse, string gethChainResponse,
        out EthereumNetworkType networkType, out GethChainType chainType)
    {
        // convert network
        if(int.TryParse(netVersionResponse, out var netWorkTypeInt))
        {
            networkType = (EthereumNetworkType) netWorkTypeInt;

            if(!Enum.IsDefined(typeof(EthereumNetworkType), networkType))
                networkType = EthereumNetworkType.Unknown;
        }

        else
            networkType = EthereumNetworkType.Unknown;

        // convert chain
        if(!Enum.TryParse(gethChainResponse, true, out chainType))
        {
            chainType = GethChainType.Unknown;
        }

        if(chainType == GethChainType.Main)
            chainType = GethChainType.Main;

        if(chainType == GethChainType.Callisto)
            chainType = GethChainType.Callisto;

        // PowLayer detection by network ID - always check this first to override the chain type
        if(networkType == (EthereumNetworkType) 70707)
            chainType = GethChainType.PowLayer;

        // ZapChain detection by network ID - always check this first to override the chain type
        if(networkType == (EthereumNetworkType) 757)
            chainType = GethChainType.ZapChain;

        // Etica detection by network ID - always check this first to override the chain type
        if(networkType == (EthereumNetworkType) 61803)
            chainType = GethChainType.Etica;

        // Thoreum detection by network ID - always check this first to override the chain type
        if(networkType == (EthereumNetworkType) 357)
            chainType = GethChainType.Thoreum;

        // Parallax detection by network ID - always check this first to override the chain type
        if(networkType == (EthereumNetworkType) 2110)
            chainType = GethChainType.Parallax;
    }
}
