/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { DEMOTRACER_CREDITS } from "./credits.ts";

test("credits preserve the approved creator and contributor roster", () => {
  assert.equal(DEMOTRACER_CREDITS.creator.name, "unicbm");
  assert.deepEqual(DEMOTRACER_CREDITS.contributors.map(({ name }) => name), [
    "ed0ard",
    "Newbie046",
    "XBribo",
    "Misaka17032",
    "T1mLuk0",
  ]);
});

test("every named person has a verified GitHub avatar and profile source", () => {
  const people = [DEMOTRACER_CREDITS.creator, ...DEMOTRACER_CREDITS.contributors];
  for (const person of people) {
    assert.match(person.githubHandle, /^[A-Za-z\d](?:[A-Za-z\d-]{0,37}[A-Za-z\d])?$/);
    assert.match(person.avatarUrl, /^https:\/\/avatars\.githubusercontent\.com\/u\/\d+\?v=4$/);
    assert.equal(person.profileUrl, `https://github.com/${person.githubHandle}`);
  }
  for (const foundation of DEMOTRACER_CREDITS.foundations) {
    assert.match(foundation.avatarUrl, /^https:\/\/avatars\.githubusercontent\.com\/u\/\d+\?v=4$/);
    assert.equal(foundation.profileUrl, `https://github.com/${foundation.githubHandle}`);
  }
});

test("credits retain the foundational upstream projects", () => {
  assert.deepEqual(
    DEMOTRACER_CREDITS.foundations.map(({ id, projects }) => ({
      id,
      repositories: projects.map(({ repository }) => repository),
    })),
    [
      {
        id: "xbribo",
        repositories: ["XBribo/CS2-Bot-Controller", "XBribo/CS2-Bot-Hider"],
      },
      {
        id: "ianLucas",
        repositories: [
          "ianlucas/cs2-inventory-simulator",
          "ianlucas/cs2-lib",
          "ianlucas/cs2-lib-inspect",
        ],
      },
      {
        id: "demoparser",
        repositories: ["LaihoE/demoparser"],
      },
    ],
  );

  for (const foundation of DEMOTRACER_CREDITS.foundations) {
    for (const project of foundation.projects) {
      assert.equal(project.url, `https://github.com/${project.repository}`);
    }
  }
});
