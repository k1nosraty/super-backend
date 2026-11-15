import assert from "node:assert/strict";
import { chromium } from "playwright";
import crypto from "node:crypto";
import fs from "node:fs";

const fixture = JSON.parse(fs.readFileSync(process.env.EVIDENCE_DIRECTORY + "/private-session.json"));
const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
let bits = 0;
let buffer = 0;
const secret = [];
for (const character of fixture.key.toUpperCase().replaceAll("=", "")) {
    buffer = (buffer << 5) | alphabet.indexOf(character);
    bits += 5;
    if (bits >= 8) {
        bits -= 8;
        secret.push((buffer >>> bits) & 255);
    }
}
const timestamp = Buffer.alloc(8);
timestamp.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
const digest = crypto.createHmac("sha1", Buffer.from(secret)).update(timestamp).digest();
const code = String((digest.readUInt32BE(digest.at(-1) & 15) & 0x7fffffff) % 1000000).padStart(6, "0");
const browser = await chromium.launch({
    ...(process.env.CHROMIUM_EXECUTABLE ? { executablePath: process.env.CHROMIUM_EXECUTABLE } : {})
});
try {
    const page = await browser.newPage({ ignoreHTTPSErrors: true });
    await page.goto(process.env.PUBLIC_ORIGIN + "/admin");
    const form = page.locator("#login-form");
    async function login(email, mfa = false) {
        await form.locator('input[name="email"]').fill(email);
        await form.locator('input[name="password"]').fill(process.env.REHEARSAL_PASSWORD);
        if (mfa) {
            if (await page.locator("#mfa").getAttribute("open") === null) {
                await page.locator("#mfa summary").click();
            }
            await form.locator('input[name="twoFactorCode"]').fill(code);
        }
        await form.locator("button[type=submit]").click();
    }

    await login("member@example.test");
    await page.locator("#login-error").waitFor({ state: "visible" });
    assert.match(await page.locator("#login-error").textContent(), /Administrator access is required/);
    assert.equal(await page.locator("#workspace").isVisible(), false);

    const authorization = page.waitForRequest(request => request.url().endsWith("/api/v1/admin/permissions"));
    await login("operator@example.test", true);
    await page.locator("#workspace").waitFor({ state: "visible" });
    const bearer = (await authorization).headers().authorization;
    const revoked = await page.request.post(process.env.PUBLIC_ORIGIN + "/api/v1/auth/logout", {
        headers: { Authorization: bearer }
    });
    assert.equal(revoked.status(), 204);
    await page.locator('[data-view="roles"]').click();
    await page.locator("#login-screen").waitFor({ state: "visible" });
    assert.match(await page.locator("#login-error").textContent(), /The session has ended/);

    await login("operator@example.test", true);
    await page.locator("#workspace").waitFor({ state: "visible" });
    const language = page.locator("#language");
    await language.focus();
    await page.keyboard.press("End");
    await page.keyboard.press("Enter");
    await page.waitForFunction(() => document.documentElement.dir === "rtl");
    assert.equal(await page.locator("html").getAttribute("lang"), "fa");
    await page.locator("#logout").focus();
    await page.keyboard.press("Enter");
    await page.locator("#login-screen").waitFor({ state: "visible" });
    console.log("Browser MFA, member denial, expired-session feedback, RTL keyboard navigation, and logout verified.");
} finally {
    await browser.close();
}
