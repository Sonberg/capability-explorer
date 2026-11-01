#!/usr/bin/env python3
"""Opt-in disposable PG17 smoke/recovery probe. No models, ports or existing DBs."""
import argparse, json, pathlib, platform, shutil, subprocess, time, uuid

BASELINE = 'pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f'
CANDIDATE = 'ruvnet/ruvector-postgres@sha256:7fb09d439d82fccbe6e0035d09f91c9dce456bb2d70cb2e8d6afb12a6afa2051'
SOURCE = 'cf357d8121f9def5bc9d2db512a47f61c62b0651'

def run(args, text=None, timeout=60, check=True):
    result = subprocess.run(args, input=text, capture_output=True, text=True, timeout=timeout)
    if check and result.returncode:
        raise RuntimeError((result.stderr or result.stdout)[-3000:])
    return result

def smoke_status(checks):
    if not checks:
        return 'NOT_EVALUATED'
    return 'PASS_SMOKE_ONLY' if all(v['status'] == 'PASS' for v in checks.values()) else 'FAIL'

def probe(image, extension, platform_arg):
    name = 'ce-issue28-probe-' + uuid.uuid4().hex[:12]
    result = {'image': image, 'extension': extension, 'platform': platform_arg, 'checks': {}, 'status': 'NOT_EVALUATED'}
    def sql(query, check=True):
        return run(['docker', 'exec', '-i', name, 'psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-U', 'probe', '-d', 'probe'], query, check=check)
    def observe(key, query, expected=None):
        response = sql(query, False)
        actual = response.stdout.strip()
        result['checks'][key] = {'status': 'PASS' if response.returncode == 0 and (expected is None or expected == actual) else 'FAIL', 'actual': actual, 'expected': expected, 'error': response.stderr.strip()[-1000:]}
        return actual
    try:
        info = json.loads(run(['docker', 'image', 'inspect', image]).stdout)[0]
        result['image_id'] = info['Id']; result['image_bytes'] = info['Size']
        result['oci_labels'] = info['Config'].get('Labels'); result['architecture'] = info['Architecture']
        run(['docker', 'run', '-d', '--pull=never', '--platform', platform_arg,
             '--name', name, '--label', 'ce.experiment=issue28-ruvector', '--network', 'none',
             '--cpus', '1', '--memory', '512m', '--pids-limit', '128',
             '--tmpfs', '/docker-entrypoint-initdb.d:ro',
             '-e', 'POSTGRES_USER=probe', '-e', 'POSTGRES_PASSWORD=disposable-local-only',
             '-e', 'POSTGRES_DB=probe', image, 'postgres', '-c', 'shared_buffers=32MB', '-c', 'max_connections=10'])
        for _ in range(120):
            ready = run(['docker', 'exec', name, 'pg_isready', '-U', 'probe'], check=False)
            final_server = run(['docker', 'exec', name, 'sh', '-c', 'test "$(cat /proc/1/comm)" = postgres'], check=False)
            if ready.returncode == 0 and final_server.returncode == 0:
                break
            time.sleep(.25)
        else:
            raise RuntimeError('Final PostgreSQL server did not become ready')
        sql('CREATE EXTENSION ' + extension + ';')
        result['extension_version'] = sql("SELECT extversion FROM pg_extension WHERE extname='" + extension + "';").stdout.strip()
        result['server_version'] = sql('SHOW server_version;').stdout.strip()
        # Common relational contract, not a performance comparison or app integration test.
        sql('CREATE TABLE facts(id bigint PRIMARY KEY,name text NOT NULL); CREATE TABLE edges(src bigint REFERENCES facts,id bigint PRIMARY KEY,dst bigint REFERENCES facts); INSERT INTO facts SELECT n,\'symbol-\'||n FROM generate_series(1,1000)n; INSERT INTO edges SELECT n,n,n+1 FROM generate_series(1,999)n; CREATE INDEX ON edges(src,id);')
        observe('exact_bounded_lookup', 'SELECT id FROM facts WHERE name LIKE \'symbol-99%\' ORDER BY id LIMIT 3;', '99\n990\n991')
        observe('bounded_adjacency', 'SELECT dst FROM edges WHERE src=99 ORDER BY id LIMIT 25;', '100')
        observe('relational_rollback', "BEGIN; INSERT INTO facts VALUES(1001,'rollback'); ROLLBACK; SELECT count(*) FROM facts WHERE id=1001;", '0')
        if extension == 'ruvector':
            observe('pgvector_extension_available', "SELECT count(*) FROM pg_available_extensions WHERE name='vector';", '1')
            observe('real_array_vector_cast', "SELECT ARRAY[1,0,0]::real[]::vector;")
            observe('graph_seed', "SELECT ruvector_create_graph('probe'); SELECT ruvector_add_node('probe',ARRAY['Symbol'],'{\"name\":\"one\"}'::jsonb);")
            observe('graph_persists_new_session', "SELECT ruvector_graph_stats('probe')->>'node_count';", '1')
            # Same backend must forget rolled-back mutations, not only persisted SQL rows.
            observe('graph_same_session_rollback', "BEGIN; SELECT ruvector_add_node('probe',ARRAY['Symbol'],'{}'::jsonb) IS NOT NULL; ROLLBACK; SELECT ruvector_graph_stats('probe')->>'node_count';", 't\n1')
            observe('graph_after_rollback_new_session', "SELECT ruvector_graph_stats('probe')->>'node_count';", '1')
        vector_type = 'vector' if extension == 'vector' else 'ruvector'
        observe('fixed_vector_distance', "SELECT ('[1,0,0]'::"+vector_type+" <-> '[0,1,0]'::"+vector_type+") BETWEEN 1.4142 AND 1.4143;", 't')
        run(['docker', 'restart', '-t', '10', name])
        for _ in range(120):
            if run(['docker', 'exec', name, 'pg_isready', '-U', 'probe'], check=False).returncode == 0: break
            time.sleep(.25)
        observe('clean_restart_relational', 'SELECT count(*) FROM facts;', '1000')
        if extension == 'ruvector': observe('clean_restart_graph', "SELECT ruvector_graph_stats('probe')->>'node_count';", '1')
        run(['docker', 'kill', '--signal=KILL', name]); run(['docker', 'start', name])
        for _ in range(120):
            if run(['docker', 'exec', name, 'pg_isready', '-U', 'probe'], check=False).returncode == 0: break
            time.sleep(.25)
        observe('crash_restart_relational', 'SELECT count(*) FROM facts;', '1000')
        if extension == 'ruvector': observe('crash_restart_graph', "SELECT ruvector_graph_stats('probe')->>'node_count';", '1')
        result['idle_stats'] = run(['docker', 'stats', '--no-stream', '--format', '{{json .}}', name]).stdout.strip()
        result['status'] = smoke_status(result['checks'])
    except Exception as error:
        result['failure'] = str(error)
    finally:
        result['container_logs_tail'] = run(['docker', 'logs', '--tail', '35', name], check=False).stderr[-4000:]
        # Own UUID-labelled container only; -v removes its anonymous experiment volume.
        run(['docker', 'rm', '-fv', name], check=False)
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', default='.sandbox-cache/issue28-ruvector-probe.json')
    parser.add_argument('--candidate-only', action='store_true')
    args = parser.parse_args()
    if shutil.disk_usage('.').free < 8*1024**3: raise SystemExit('Need 8 GiB disk reserve')
    report = {'schema': 1, 'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()), 'host_machine':platform.machine(), 'inspected_source':SOURCE, 'source_binary_lineage':'UNVERIFIED: image has no revision label', 'purpose':'SMOKE: not adoption/performance/security proof', 'paid_model_calls':0, 'results':[]}
    if not args.candidate_only: report['results'].append(probe(BASELINE,'vector','linux/arm64'))
    report['results'].append(probe(CANDIDATE,'ruvector','linux/amd64'))
    target=pathlib.Path(args.output); target.parent.mkdir(parents=True,exist_ok=True)
    target.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'report':str(target),'statuses':[r['status'] for r in report['results']]}))
    # Fail closed: smoke success never qualifies the candidate for adoption.
    return 0 if all(r['status']=='PASS_SMOKE_ONLY' for r in report['results']) else 1

if __name__ == '__main__': raise SystemExit(main())
