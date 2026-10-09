import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    testTimeout: 10000,
    hookTimeout: 10000,
  },
  server: {
    port: 3000,
    proxy: {
      "/api": {
        target: "http://localhost:9550",
        changeOrigin: true,
      },
      "/ws": {
        target: "ws://localhost:9550",
        ws: true,
      },
    },
  },
});
