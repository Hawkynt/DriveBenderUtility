import com.backblaze.erasure.OutputInputByteTableCodingLoop;
import com.backblaze.erasure.ReedSolomon;

import java.io.FileOutputStream;
import java.io.IOException;

/**
 * Reference parity generator for the C# codec cross-check.
 *
 * Usage: OracleMain seed dataShards parityShards shardSize outputFile
 *
 * Fills the data shards from a 64-bit LCG (the same one the C# test uses), encodes with the
 * unmodified Backblaze JavaReedSolomon, and writes the parity shards back to back to outputFile.
 */
public final class OracleMain {

    public static void main(String[] args) throws IOException {
        long state = Long.parseLong(args[0]);
        int dataShards = Integer.parseInt(args[1]);
        int parityShards = Integer.parseInt(args[2]);
        int shardSize = Integer.parseInt(args[3]);
        String outputFile = args[4];

        byte[][] shards = new byte[dataShards + parityShards][shardSize];
        for (int i = 0; i < dataShards; i++) {
            for (int j = 0; j < shardSize; j++) {
                state = state * 6364136223846793005L + 1442695040888963407L;
                shards[i][j] = (byte) (state >>> 56);
            }
        }

        ReedSolomon codec = new ReedSolomon(dataShards, parityShards, new OutputInputByteTableCodingLoop());
        codec.encodeParity(shards, 0, shardSize);

        try (FileOutputStream out = new FileOutputStream(outputFile)) {
            for (int i = dataShards; i < dataShards + parityShards; i++) {
                out.write(shards[i]);
            }
        }
    }
}
