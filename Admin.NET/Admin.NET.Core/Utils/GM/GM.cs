// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.GM;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Utilities.Encoders;
using Org.BouncyCastle.X509;

namespace Admin.NET.Core;

/**
 *
 * 用BC的注意点：
 * 这个版本的BC对SM3withSM2的结果为asn1格式的r和s，如果需要直接拼接的r||s需要自己转换。下面rsAsn1ToPlainByteArray、rsPlainByteArrayToAsn1就在干这事。
 * 这个版本的BC对SM2的结果为C1||C2||C3，据说为旧标准，新标准为C1||C3||C2，用新标准的需要自己转换。下面（被注释掉的）changeC1C2C3ToC1C3C2、changeC1C3C2ToC1C2C3就在干这事。java版的高版本有加上C1C3C2，csharp版没准以后也会加，但目前还没有，java版的目前可以初始化时“ SM2Engine sm2Engine = new SM2Engine(SM2Engine.Mode.C1C3C2);”。
 *
 */

public class GM
{
    private static X9ECParameters x9ECParameters = GMNamedCurves.GetByName("sm2p256v1");
    private static ECDomainParameters ecDomainParameters = new(x9ECParameters.Curve, x9ECParameters.G, x9ECParameters.N);

    /**
     *
     * @param msg
     * @param userId
     * @param privateKey
     * @return r||s，直接拼接byte数组的rs
     */

    /// <summary>根据 GM 当前输入计算 Sign Sm3 With Sm2 结果，并按方法声明的类型返回。</summary>
    /// <param name="msg">msg 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="userId">需要定位的业务记录标识。</param>
    /// <param name="privateKey">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] SignSm3WithSm2(byte[] msg, byte[] userId, AsymmetricKeyParameter privateKey)
    {
        return RsAsn1ToPlainByteArray(SignSm3WithSm2Asn1Rs(msg, userId, privateKey));
    }

    /**
      * @param msg
      * @param userId
      * @param privateKey
      * @return rs in <b>asn1 format</b>
      */

    /// <summary>根据 GM 当前输入计算 Sign Sm3 With Sm2 Asn1 Rs 结果，并按方法声明的类型返回。</summary>
    /// <param name="msg">msg 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="userId">需要定位的业务记录标识。</param>
    /// <param name="privateKey">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] SignSm3WithSm2Asn1Rs(byte[] msg, byte[] userId, AsymmetricKeyParameter privateKey)
    {
        ISigner signer = SignerUtilities.GetSigner("SM3withSM2");
        signer.Init(true, new ParametersWithID(privateKey, userId));
        signer.BlockUpdate(msg, 0, msg.Length);
        byte[] sig = signer.GenerateSignature();
        return sig;
    }

    /**
    *
    * @param msg
    * @param userId
    * @param rs r||s，直接拼接byte数组的rs
    * @param publicKey
    * @return
    */

    /// <summary>校验 GM 的 Verify Sm3 With Sm2 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="msg">msg 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="userId">需要定位的业务记录标识。</param>
    /// <param name="rs">rs 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="publicKey">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static bool VerifySm3WithSm2(byte[] msg, byte[] userId, byte[] rs, AsymmetricKeyParameter publicKey)
    {
        if (rs == null || msg == null || userId == null) return false;
        if (rs.Length != RS_LEN * 2) return false;
        return VerifySm3WithSm2Asn1Rs(msg, userId, RsPlainByteArrayToAsn1(rs), publicKey);
    }

    /**
     *
     * @param msg
     * @param userId
     * @param rs in <b>asn1 format</b>
     * @param publicKey
     * @return
     */

    /// <summary>校验 GM 的 Verify Sm3 With Sm2 Asn1 Rs 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="msg">msg 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="userId">需要定位的业务记录标识。</param>
    /// <param name="sign">sign 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="publicKey">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static bool VerifySm3WithSm2Asn1Rs(byte[] msg, byte[] userId, byte[] sign, AsymmetricKeyParameter publicKey)
    {
        ISigner signer = SignerUtilities.GetSigner("SM3withSM2");
        signer.Init(false, new ParametersWithID(publicKey, userId));
        signer.BlockUpdate(msg, 0, msg.Length);
        return signer.VerifySignature(sign);
    }

    /**
     * bc加解密使用旧标c1||c2||c3，此方法在加密后调用，将结果转化为c1||c3||c2
     * @param c1c2c3
     * @return
     */

    /// <summary>更新 GM 的 Change C1 C2 C3 To C1 C3 C2 状态，并同步相关业务数据。</summary>
    /// <param name="c1c2c3">c1c2c3 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] ChangeC1C2C3ToC1C3C2(byte[] c1c2c3)
    {
        int c1Len = (x9ECParameters.Curve.FieldSize + 7) / 8 * 2 + 1; //sm2p256v1的这个固定65。可看GMNamedCurves、ECCurve代码。
        const int c3Len = 32; //new SM3Digest().getDigestSize();
        byte[] result = new byte[c1c2c3.Length];
        Buffer.BlockCopy(c1c2c3, 0, result, 0, c1Len); //c1
        Buffer.BlockCopy(c1c2c3, c1c2c3.Length - c3Len, result, c1Len, c3Len); //c3
        Buffer.BlockCopy(c1c2c3, c1Len, result, c1Len + c3Len, c1c2c3.Length - c1Len - c3Len); //c2
        return result;
    }

    /**
     * bc加解密使用旧标c1||c3||c2，此方法在解密前调用，将密文转化为c1||c2||c3再去解密
     * @param c1c3c2
     * @return
     */

    /// <summary>更新 GM 的 Change C1 C3 C2 To C1 C2 C3 状态，并同步相关业务数据。</summary>
    /// <param name="c1c3c2">c1c3c2 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] ChangeC1C3C2ToC1C2C3(byte[] c1c3c2)
    {
        int c1Len = (x9ECParameters.Curve.FieldSize + 7) / 8 * 2 + 1; //sm2p256v1的这个固定65。可看GMNamedCurves、ECCurve代码。
        const int c3Len = 32; //new SM3Digest().GetDigestSize();
        byte[] result = new byte[c1c3c2.Length];
        Buffer.BlockCopy(c1c3c2, 0, result, 0, c1Len); //c1: 0->65
        Buffer.BlockCopy(c1c3c2, c1Len + c3Len, result, c1Len, c1c3c2.Length - c1Len - c3Len); //c2
        Buffer.BlockCopy(c1c3c2, c1Len, result, c1c3c2.Length - c3Len, c3Len); //c3
        return result;
    }

    /**
     * c1||c3||c2
     * @param data
     * @param key
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Sm2 Decrypt 结果，并按方法声明的类型返回。</summary>
    /// <param name="data">本次操作使用的输入数据。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm2Decrypt(byte[] data, AsymmetricKeyParameter key)
    {
        return Sm2DecryptOld(ChangeC1C3C2ToC1C2C3(data), key);
    }

    /**
     * c1||c3||c2
     * @param data
     * @param key
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Sm2 Encrypt 结果，并按方法声明的类型返回。</summary>
    /// <param name="data">本次操作使用的输入数据。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm2Encrypt(byte[] data, AsymmetricKeyParameter key)
    {
        return ChangeC1C2C3ToC1C3C2(Sm2EncryptOld(data, key));
    }

    /**
     * c1||c2||c3
     * @param data
     * @param key
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Sm2 Encrypt Old 结果，并按方法声明的类型返回。</summary>
    /// <param name="data">本次操作使用的输入数据。</param>
    /// <param name="pubkey">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm2EncryptOld(byte[] data, AsymmetricKeyParameter pubkey)
    {
        SM2Engine sm2Engine = new SM2Engine();
        sm2Engine.Init(true, new ParametersWithRandom(pubkey, new SecureRandom()));
        return sm2Engine.ProcessBlock(data, 0, data.Length);
    }

    /**
     * c1||c2||c3
     * @param data
     * @param key
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Sm2 Decrypt Old 结果，并按方法声明的类型返回。</summary>
    /// <param name="data">本次操作使用的输入数据。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm2DecryptOld(byte[] data, AsymmetricKeyParameter key)
    {
        SM2Engine sm2Engine = new SM2Engine();
        sm2Engine.Init(false, key);
        return sm2Engine.ProcessBlock(data, 0, data.Length);
    }

    /**
     * @param bytes
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Sm3 结果，并按方法声明的类型返回。</summary>
    /// <param name="bytes">bytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm3(byte[] bytes)
    {
        SM3Digest digest = new();
        digest.BlockUpdate(bytes, 0, bytes.Length);
        byte[] result = DigestUtilities.DoFinal(digest);
        return result;
    }

    private const int RS_LEN = 32;

    /// <summary>检查 GM 的 Big Int To Fixex Length Bytes 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="rOrS">rOrS 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] BigIntToFixexLengthBytes(BigInteger rOrS)
    {
        // for sm2p256v1, n is 00fffffffeffffffffffffffffffffffff7203df6b21c6052b53bbf40939d54123,
        // r and s are the result of mod n, so they should be less than n and have length<=32
        byte[] rs = rOrS.ToByteArray();
        if (rs.Length == RS_LEN) return rs;
        else if (rs.Length == RS_LEN + 1 && rs[0] == 0) return Arrays.CopyOfRange(rs, 1, RS_LEN + 1);
        else if (rs.Length < RS_LEN)
        {
            byte[] result = new byte[RS_LEN];
            Arrays.Fill(result, (byte)0);
            Buffer.BlockCopy(rs, 0, result, RS_LEN - rs.Length, rs.Length);
            return result;
        }
        else
        {
            throw new ArgumentException("err rs: " + Hex.ToHexString(rs));
        }
    }

    /**
     * BC的SM3withSM2签名得到的结果的rs是asn1格式的，这个方法转化成直接拼接r||s
     * @param rsDer rs in asn1 format
     * @return sign result in plain byte array
     */

    /// <summary>根据 GM 当前输入计算 Rs Asn1 To Plain Byte Array 结果，并按方法声明的类型返回。</summary>
    /// <param name="rsDer">rsDer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] RsAsn1ToPlainByteArray(byte[] rsDer)
    {
        Asn1Sequence seq = Asn1Sequence.GetInstance(rsDer);
        byte[] r = BigIntToFixexLengthBytes(DerInteger.GetInstance(seq[0]).Value);
        byte[] s = BigIntToFixexLengthBytes(DerInteger.GetInstance(seq[1]).Value);
        byte[] result = new byte[RS_LEN * 2];
        Buffer.BlockCopy(r, 0, result, 0, r.Length);
        Buffer.BlockCopy(s, 0, result, RS_LEN, s.Length);
        return result;
    }

    /**
     * BC的SM3withSM2验签需要的rs是asn1格式的，这个方法将直接拼接r||s的字节数组转化成asn1格式
     * @param sign in plain byte array
     * @return rs result in asn1 format
     */

    /// <summary>检查 GM 的 Rs Plain Byte Array To Asn1 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="sign">sign 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] RsPlainByteArrayToAsn1(byte[] sign)
    {
        if (sign.Length != RS_LEN * 2) throw new ArgumentException("err rs. ");
        BigInteger r = new BigInteger(1, Arrays.CopyOfRange(sign, 0, RS_LEN));
        BigInteger s = new BigInteger(1, Arrays.CopyOfRange(sign, RS_LEN, RS_LEN * 2));
        Asn1EncodableVector v = new Asn1EncodableVector
        {
            new DerInteger(r),
            new DerInteger(s)
        };

        return new DerSequence(v).GetEncoded("DER");
    }

    // 生成公私匙对
    /// <summary>构建 GM 所需的 Generate Key Pair 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static AsymmetricCipherKeyPair GenerateKeyPair()
    {
        ECKeyPairGenerator kpGen = new();
        kpGen.Init(new ECKeyGenerationParameters(ecDomainParameters, new SecureRandom()));
        return kpGen.GenerateKeyPair();
    }

    /// <summary>查询或读取 GM 所需的 Get Privatekey From D 数据，并按调用约定返回结果。</summary>
    /// <param name="d">d 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static ECPrivateKeyParameters GetPrivatekeyFromD(BigInteger d)
    {
        return new ECPrivateKeyParameters(d, ecDomainParameters);
    }

    /// <summary>查询或读取 GM 所需的 Get Publickey From XY 数据，并按调用约定返回结果。</summary>
    /// <param name="x">x 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="y">y 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static ECPublicKeyParameters GetPublickeyFromXY(BigInteger x, BigInteger y)
    {
        return new ECPublicKeyParameters(x9ECParameters.Curve.CreatePoint(x, y), ecDomainParameters);
    }

    /// <summary>查询或读取 GM 所需的 Get Publickey From X509 File 数据，并按调用约定返回结果。</summary>
    /// <param name="file">file 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static AsymmetricKeyParameter GetPublickeyFromX509File(FileInfo file)
    {
        FileStream fileStream = null;
        try
        {
            //file.DirectoryName + "\\" + file.Name
            fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read);
            X509Certificate certificate = new X509CertificateParser().ReadCertificate(fileStream);
            return certificate.GetPublicKey();
        }
        catch (Exception)
        {
            //log.Error(file.Name + "读取失败，异常：" + e);
        }
        finally
        {
            if (fileStream != null)
                fileStream.Close();
        }
        return null;
    }

    public class Sm2Cert
    {
        public AsymmetricKeyParameter privateKey;
        public AsymmetricKeyParameter publicKey;
        public string certId;
    }

    /// <summary>根据 GM 当前输入计算 To Byte Array 结果，并按方法声明的类型返回。</summary>
    /// <param name="i">i 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] ToByteArray(int i)
    {
        byte[] byteArray = new byte[4];
        byteArray[0] = (byte)(i >> 24);
        byteArray[1] = (byte)((i & 0xFFFFFF) >> 16);
        byteArray[2] = (byte)((i & 0xFFFF) >> 8);
        byteArray[3] = (byte)(i & 0xFF);
        return byteArray;
    }

    /**
     * 字节数组拼接
     *
     * @param params
     * @return
     */

    /// <summary>根据 GM 当前输入计算 Join 结果，并按方法声明的类型返回。</summary>
    /// <param name="byteArrays">byteArrays 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] Join(params byte[][] byteArrays)
    {
        List<byte> byteSource = new();
        for (int i = 0; i < byteArrays.Length; i++)
        {
            byteSource.AddRange(byteArrays[i]);
        }
        byte[] data = byteSource.ToArray();
        return data;
    }

    /**
     * 密钥派生函数
     *
     * @param Z
     * @param klen
     * 生成klen字节数长度的密钥
     * @return
     */

    /// <summary>根据 GM 当前输入计算 KDF 结果，并按方法声明的类型返回。</summary>
    /// <param name="Z">Z 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="klen">klen 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] KDF(byte[] Z, int klen)
    {
        int ct = 1;
        int end = (int)Math.Ceiling(klen * 1.0 / 32);
        List<byte> byteSource = new();

        for (int i = 1; i < end; i++)
        {
            byteSource.AddRange(Sm3(Join(Z, ToByteArray(ct))));
            ct++;
        }
        byte[] last = Sm3(Join(Z, ToByteArray(ct)));
        if (klen % 32 == 0)
        {
            byteSource.AddRange(last);
        }
        else
            byteSource.AddRange(Arrays.CopyOfRange(last, 0, klen % 32));
        return byteSource.ToArray();
    }

    /// <summary>检查 GM 的 Sm4 Decrypt CBC 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="keyBytes">keyBytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cipher">cipher 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="iv">iv 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="algo">algo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm4DecryptCBC(byte[] keyBytes, byte[] cipher, byte[] iv, string algo)
    {
        if (keyBytes.Length != 16) throw new ArgumentException("err key length");
        if (cipher.Length % 16 != 0 && algo.Contains("NoPadding")) throw new ArgumentException("err data length");

        KeyParameter key = ParameterUtilities.CreateKeyParameter("SM4", keyBytes);
        IBufferedCipher c = CipherUtilities.GetCipher(algo);
        if (iv == null) iv = ZeroIv(algo);
        c.Init(false, new ParametersWithIV(key, iv));
        return c.DoFinal(cipher);
    }

    /// <summary>检查 GM 的 Sm4 Encrypt CBC 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="keyBytes">keyBytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="plain">plain 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="iv">iv 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="algo">algo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm4EncryptCBC(byte[] keyBytes, byte[] plain, byte[] iv, string algo)
    {
        if (keyBytes.Length != 16) throw new ArgumentException("err key length");
        if (plain.Length % 16 != 0 && algo.Contains("NoPadding")) throw new ArgumentException("err data length");

        KeyParameter key = ParameterUtilities.CreateKeyParameter("SM4", keyBytes);
        IBufferedCipher c = CipherUtilities.GetCipher(algo);
        if (iv == null) iv = ZeroIv(algo);
        c.Init(true, new ParametersWithIV(key, iv));
        return c.DoFinal(plain);
    }

    /// <summary>检查 GM 的 Sm4 Encrypt ECB 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="keyBytes">keyBytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="plain">plain 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="algo">algo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm4EncryptECB(byte[] keyBytes, byte[] plain, string algo)
    {
        if (keyBytes.Length != 16) throw new ArgumentException("err key length");
        //NoPadding 的情况下需要校验数据长度是16的倍数.
        if (plain.Length % 16 != 0 && algo.Contains("NoPadding")) throw new ArgumentException("err data length");

        KeyParameter key = ParameterUtilities.CreateKeyParameter("SM4", keyBytes);
        IBufferedCipher c = CipherUtilities.GetCipher(algo);
        c.Init(true, key);
        return c.DoFinal(plain);
    }

    /// <summary>检查 GM 的 Sm4 Decrypt ECB 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="keyBytes">keyBytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cipher">cipher 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="algo">algo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] Sm4DecryptECB(byte[] keyBytes, byte[] cipher, string algo)
    {
        if (keyBytes.Length != 16) throw new ArgumentException("err key length");
        if (cipher.Length % 16 != 0 && algo.Contains("NoPadding")) throw new ArgumentException("err data length");

        KeyParameter key = ParameterUtilities.CreateKeyParameter("SM4", keyBytes);
        IBufferedCipher c = CipherUtilities.GetCipher(algo);
        c.Init(false, key);
        return c.DoFinal(cipher);
    }

    public const string SM4_ECB_NOPADDING = "SM4/ECB/NoPadding";
    public const string SM4_CBC_NOPADDING = "SM4/CBC/NoPadding";
    public const string SM4_ECB_PKCS7PADDING = "SM4/ECB/PKCS7Padding";
    public const string SM4_CBC_PKCS7PADDING = "SM4/CBC/PKCS7Padding";

    /**
     * cfca官网CSP沙箱导出的sm2文件
     * @param pem 二进制原文
     * @param pwd 密码
     * @return
     */

    /// <summary>查询或读取 GM 所需的 Read Sm2 File 数据，并按调用约定返回结果。</summary>
    /// <param name="pem">pem 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pwd">pwd 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static Sm2Cert ReadSm2File(byte[] pem, string pwd)
    {
        Sm2Cert sm2Cert = new();

        Asn1Sequence asn1Sequence = (Asn1Sequence)Asn1Object.FromByteArray(pem);
        //            ASN1Integer asn1Integer = (ASN1Integer) asn1Sequence.getObjectAt(0); //version=1
        Asn1Sequence priSeq = (Asn1Sequence)asn1Sequence[1];//private key
        Asn1Sequence pubSeq = (Asn1Sequence)asn1Sequence[2];//public key and x509 cert

        //            ASN1ObjectIdentifier sm2DataOid = (ASN1ObjectIdentifier) priSeq.getObjectAt(0);
        //            ASN1ObjectIdentifier sm4AlgOid = (ASN1ObjectIdentifier) priSeq.getObjectAt(1);
        Asn1OctetString priKeyAsn1 = (Asn1OctetString)priSeq[2];
        byte[] key = KDF(System.Text.Encoding.UTF8.GetBytes(pwd), 32);
        byte[] priKeyD = Sm4DecryptCBC(Arrays.CopyOfRange(key, 16, 32),
                priKeyAsn1.GetOctets(),
                Arrays.CopyOfRange(key, 0, 16), SM4_CBC_PKCS7PADDING);
        sm2Cert.privateKey = GetPrivatekeyFromD(new BigInteger(1, priKeyD));
        //            log.Info(Hex.toHexString(priKeyD));

        //            ASN1ObjectIdentifier sm2DataOidPub = (ASN1ObjectIdentifier) pubSeq.getObjectAt(0);
        Asn1OctetString pubKeyX509 = (Asn1OctetString)pubSeq[1];
        X509Certificate x509 = new X509CertificateParser().ReadCertificate(pubKeyX509.GetOctets());
        sm2Cert.publicKey = x509.GetPublicKey();
        sm2Cert.certId = x509.SerialNumber.ToString(10); //这里转10进制，有啥其他进制要求的自己改改
        return sm2Cert;
    }

    /**
     *
     * @param cert
     * @return
     */

    /// <summary>查询或读取 GM 所需的 Read Sm2 X509 Cert 数据，并按调用约定返回结果。</summary>
    /// <param name="cert">cert 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static Sm2Cert ReadSm2X509Cert(byte[] cert)
    {
        Sm2Cert sm2Cert = new();

        X509Certificate x509 = new X509CertificateParser().ReadCertificate(cert);
        sm2Cert.publicKey = x509.GetPublicKey();
        sm2Cert.certId = x509.SerialNumber.ToString(10); //这里转10进制，有啥其他进制要求的自己改改
        return sm2Cert;
    }

    /// <summary>根据 GM 当前输入计算 Zero Iv 结果，并按方法声明的类型返回。</summary>
    /// <param name="algo">algo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static byte[] ZeroIv(string algo)
    {
        IBufferedCipher cipher = CipherUtilities.GetCipher(algo);
        int blockSize = cipher.GetBlockSize();
        byte[] iv = new byte[blockSize];
        Arrays.Fill(iv, (byte)0);
        return iv;
    }
}