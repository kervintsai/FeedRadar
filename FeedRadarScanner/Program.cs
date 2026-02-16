using System;
using System.Linq;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var scanner = new LovecatScanner();

        var urls = await scanner.ScanAsync(
            "https://www.lovecat.com.tw/collections/%E7%8A%AC%E4%B9%BE%E7%B3%A7%E4%B8%BB%E9%A3%9F_%E5%85%A8%E9%83%A8%E5%95%86%E5%93%81-20210721151014"
        );

    }
}
