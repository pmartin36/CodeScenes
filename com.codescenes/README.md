# CodeScenes

Code-native Unity scene authoring with bidirectional code<->scene sync. Edit the scene and your
builder source updates; edit the source and the scene updates, with no manual step.

Full documentation: https://codescenes.dev

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```
https://github.com/pmartin36/CodeScenes.git?path=com.codescenes
```

Installing gives you a 14-day free trial. After that, continued use needs a license key.

## Activate

Buy a key at https://codescenes.dev, then in Unity open **CodeScenes > License**, paste the key, and
click **Activate**. One key covers up to three machines; manage them from the same window.

## Use with AI

CodeScenes ships a skill that teaches an AI coding agent how to author your scenes. To use it with
Claude Code, copy the skill into your project:

```
cp -r Packages/com.codescenes/Documentation~/codescenes-authoring .claude/skills/
```

The full authoring API reference the skill draws on is at
`Packages/com.codescenes/Documentation~/authoring-api.md`.

## Requirements

- Unity 6000.0 or newer.

## License

Proprietary. See [LICENSE.md](LICENSE.md) and [Third Party Notices.md](Third%20Party%20Notices.md).
