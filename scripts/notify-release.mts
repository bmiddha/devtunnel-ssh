import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';

export interface Release {
  id: number;
  tag_name: string;
  published_at: string | null;
  html_url: string;
  draft: boolean;
  prerelease: boolean;
}

export interface PullRequest {
  number: number;
  merge_commit_sha: string | null;
  merged_at: string | null;
}

interface NotificationPlan {
  tag: string;
  previousTag: string | undefined;
  pullNumbers: number[];
  marker: string;
  body: string;
}

interface Label {
  name: string;
}

interface Comment {
  body: string | null;
  user?: { login: string };
}

interface NotificationOptions {
  repository: string;
  dryRun?: boolean;
  request?: (endpoint: string, ...args: string[]) => unknown;
  getLogin?: () => string;
  getLabels?: (endpoint: string) => Label[];
  getComments?: (endpoint: string) => Comment[];
}

function run(command: string, args: string[]): string {
  return execFileSync(command, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).trim();
}

function git(...args: string[]): string[] {
  return run('git', args).split('\n').filter(Boolean);
}

function api<T = unknown>(endpoint: string, ...args: string[]): T {
  return JSON.parse(run('gh', ['api', endpoint, ...args]));
}

function pages<T>(endpoint: string): T[] {
  return api<T[][]>(endpoint, '--paginate', '--slurp').flat();
}

export function planNotifications(
  releases: Release[], pulls: PullRequest[], tag?: string, history = git,
): NotificationPlan[] {
  const published = releases.filter((release): release is Release & { published_at: string } =>
    !release.draft && Boolean(release.published_at))
    .sort((a, b) => a.published_at.localeCompare(b.published_at) || a.id - b.id);
  if (tag && !published.some(release => release.tag_name === tag)) {
    throw new Error(`No published release found for ${tag}`);
  }
  const cuts = published.map(release => ({
    release,
    sha: history('rev-parse', '--verify', '--end-of-options',
      `refs/tags/${release.tag_name}^{commit}`)[0],
  }));
  return cuts.flatMap(({ release, sha }, index) => {
    if (tag && release.tag_name !== tag) return [];
    const ancestors = history('rev-list', '--topo-order', sha);
    const earlier = cuts.slice(0, index);
    const previousSha = ancestors.find(commit => earlier.some(cut => cut.sha === commit));
    const previous = earlier.findLast(cut => cut.sha === previousSha);
    const commits = new Set(previousSha ? history('rev-list', sha, `^${previousSha}`) : ancestors);
    const marker = `<!-- dtssh-release:${release.tag_name} -->`;
    const kind = release.prerelease ? 'Pre-release' : 'Release';
    return [{
      tag: release.tag_name,
      previousTag: previous?.release.tag_name,
      pullNumbers: pulls.filter(pull =>
        pull.merged_at && pull.merge_commit_sha && commits.has(pull.merge_commit_sha))
        .map(pull => pull.number).sort((a, b) => a - b),
      marker,
      body: `${marker}\nThis PR is included in ${kind.toLowerCase()} [${release.tag_name}](${release.html_url}).`,
    }];
  });
}

export function postNotifications(plans: NotificationPlan[], {
  repository, dryRun = false, request = api,
  getLogin = () => api<{ data: { viewer: { login: string } } }>(
    'graphql', '--raw-field', 'query=query { viewer { login } }',
  ).data.viewer.login,
  getLabels = pages<Label>, getComments = pages<Comment>,
}: NotificationOptions): { posted: number; labeled: number } {
  const bot = 'dtssh-release-bot[bot]';
  if (!dryRun) {
    const login = getLogin();
    if (login !== bot) {
      throw new Error(`Release notifications must be posted by ${bot}; refusing to write as ${login}`);
    }
  }
  const labels = new Set(getLabels(`repos/${repository}/labels?per_page=100`)
    .map(label => label.name.toLowerCase()));
  let posted = 0;
  let labeled = 0;
  for (const plan of plans) {
    console.log(`${plan.tag} (since ${plan.previousTag ?? 'the beginning'}): ${plan.pullNumbers.map(n => `#${n}`).join(', ') || 'no PRs'}`);
    if (plan.pullNumbers.length && !labels.has(plan.tag.toLowerCase())) {
      if (dryRun) {
        console.log(`  would create label ${plan.tag}`);
      } else {
        request(`repos/${repository}/labels`, '--method', 'POST',
          '--raw-field', `name=${plan.tag}`, '--raw-field', 'color=0e8a16',
          '--raw-field', `description=Included in dtssh ${plan.tag}`);
      }
      labels.add(plan.tag.toLowerCase());
    }
    for (const number of plan.pullNumbers) {
      const issue = `repos/${repository}/issues/${number}`;
      const existingLabels = getLabels(`${issue}/labels?per_page=100`);
      if (!existingLabels.some(label => label.name.toLowerCase() === plan.tag.toLowerCase())) {
        if (dryRun) {
          console.log(`  #${number}: would add label ${plan.tag}`);
        } else {
          request(`${issue}/labels`, '--method', 'POST', '--field', `labels[]=${plan.tag}`);
          labeled++;
        }
      }
      const endpoint = `${issue}/comments`;
      const comments = getComments(`${endpoint}?per_page=100`);
      if (comments.some(comment => comment.user?.login === bot && comment.body?.includes(plan.marker))) {
        console.log(`  #${number}: already notified`);
        continue;
      }
      if (dryRun) {
        console.log(`  #${number}: would comment: ${plan.body}`);
        continue;
      }
      request(endpoint, '--method', 'POST', '--raw-field', `body=${plan.body}`);
      posted++;
      console.log(`  #${number}: notified`);
    }
  }
  console.log(`${posted} release comments posted; ${labeled} PR version labels added${dryRun ? ' (dry run)' : ''}.`);
  return { posted, labeled };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { values } = parseArgs({
    options: { tag: { type: 'string' }, 'dry-run': { type: 'boolean', default: false } },
  });
  const repository = process.env.GITHUB_REPOSITORY
    || run('gh', ['repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner']);
  const releases = pages<Release>(`repos/${repository}/releases?per_page=100`);
  const pulls = pages<PullRequest>(`repos/${repository}/pulls?state=closed&per_page=100`);
  const plans = planNotifications(releases, pulls, values.tag);
  postNotifications(plans, { repository, dryRun: values['dry-run'] });
}
