#!/usr/bin/perl
#
# The dummy proxy listener for the ADR-0015 spikes. system-proxy-macos.sh starts it; the Windows script carries the
# same logic in PowerShell. It binds 127.0.0.1 only and forwards NOTHING: every request is written down as one line
# and answered locally.
#
#   GET http://check.josour/ping?b=<label>   a 1x1 GIF: proof that <label> (a browser, the .NET probe) used the proxy
#   GET http://check.josour/                 a page saying "this browser is going through the system proxy"
#   GET http://127.0.0.1:<port>/             the poller page (pages/index.html); /webrtc and /dns are the S3 pages
#   anything else, CONNECT included          502 "not forwarded"
#
# check.josour does not exist, so a browser can only reach it by handing the request to the system proxy. Every reply
# carries "X-Josour-Spike: listener", so an ISP that answers for names that do not exist cannot pass for the listener.
#
# Log line: epoch-ms TAB kind (direct|proxy|connect) TAB label TAB target TAB user-agent
#
#   perl dummy-listener.pl PORT LOGFILE PARENT_PID PAGES_DIR
#
# It exits by itself once PARENT_PID is no longer its parent, so a killed spike script cannot leave it behind.

use strict;
use warnings;
use IO::Select;
use IO::Socket::INET;
use MIME::Base64 qw(decode_base64);
use Time::HiRes qw(time);

my ($port, $log, $parent, $pages) = @ARGV;
die "usage: perl dummy-listener.pl PORT LOGFILE PARENT_PID PAGES_DIR\n" unless defined $pages;

my $gif = decode_base64('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==');

my $server = IO::Socket::INET->new(
    LocalAddr => '127.0.0.1',
    LocalPort => $port,
    Proto     => 'tcp',
    Listen    => 128,
    ReuseAddr => 1,
) or die "cannot listen on 127.0.0.1:$port: $!\n";

$SIG{CHLD} = 'IGNORE';    # the per-connection children reap themselves
$SIG{PIPE} = 'IGNORE';
my $accepting = IO::Select->new($server);

while (1) {
    exit 0 if getppid() != $parent;
    next unless $accepting->can_read(1);
    my $client = $server->accept or next;
    my $pid = fork();
    if (!defined $pid) { close $client; next }
    if ($pid) { close $client; next }
    # A child per connection: a browser's speculative connection that never speaks must not hold up the others.
    close $server;
    serve($client);
    exit 0;
}

sub serve {
    my ($c) = @_;
    my $head = '';
    my $readable = IO::Select->new($c);
    my $deadline = time + 3;
    while ($head !~ /\r?\n\r?\n/ && length($head) < 16384) {
        my $left = $deadline - time;
        last if $left <= 0 || !$readable->can_read($left);
        my $n = sysread($c, my $buf, 4096);
        last unless $n;
        $head .= $buf;
    }
    return if $head eq '';

    my ($method, $uri) = $head =~ /^(\S+)[ \t]+(\S+)/ ? ($1, $2) : ('', '');
    my ($ua) = $head =~ /^User-Agent:[ \t]*([^\r\n]*)/mi;
    my ($host) = $head =~ /^Host:[ \t]*([^\r\n]*)/mi;
    $ua = '' unless defined $ua;
    $host = '' unless defined $host;

    my ($kind, $path) = ('direct', $uri);
    if ($method eq 'CONNECT') {
        ($kind, $host, $path) = ('connect', $uri, '');
    }
    elsif ($uri =~ m{^http://([^/?#]*)(.*)$}i) {
        ($kind, $host, $path) = ('proxy', $1, $2);
        $path = '/' if $path eq '';
    }
    my $name = lc $host;
    $name =~ s/:\d+$//;
    $name =~ s/^\[(.*)\]$/$1/;

    my ($label) = $path =~ /[?&]b=([A-Za-z0-9_.-]{1,40})/;
    # A browser in an HTTPS-only mode asks for https://check.josour/ instead: CONNECT carries no path, but a user agent.
    $label = family($ua) . '-https' if !defined $label && $kind eq 'connect' && $name eq 'check.josour';
    $label = '-' unless defined $label;

    my $target = $kind eq 'connect' ? $host : "$host$path";
    for ($target, $ua) {
        s/[\t\r\n]+/ /g;
        $_ = substr($_, 0, 200);
    }
    if (open(my $fh, '>>', $log)) {
        syswrite($fh, join("\t", int(time * 1000), $kind, $label, $target, $ua) . "\n");
        close $fh;
    }

    my $plain = $kind ne 'connect';
    my $local = $name eq '127.0.0.1' || $name eq 'localhost' || $name eq '::1';
    my $html = 'text/html; charset=utf-8';
    if    ($plain && $name eq 'check.josour' && $path =~ m{^/ping}) { reply($c, '200 OK', 'image/gif', $gif) }
    elsif ($plain && $name eq 'check.josour')                       { reply($c, '200 OK', $html, page('check.html')) }
    elsif ($plain && $local && $path =~ m{^/webrtc})                { reply($c, '200 OK', $html, page('webrtc.html')) }
    elsif ($plain && $local && $path =~ m{^/dns})                   { reply($c, '200 OK', $html, page('dns.html')) }
    elsif ($plain && $local && $path =~ m{^/(?:index\.html)?(?:\?.*)?$}) { reply($c, '200 OK', $html, page('index.html')) }
    else {
        reply($c, '502 Bad Gateway', 'text/plain; charset=utf-8',
            "Josour spike listener: not forwarded. Nothing leaves this machine through it.\n");
    }
}

sub family {
    my ($ua) = @_;
    return 'edge'    if $ua =~ m{Edg/};
    return 'firefox' if $ua =~ m{Firefox/};
    return 'chrome'  if $ua =~ m{Chrome/};
    return 'safari'  if $ua =~ m{Safari/};
    return 'other';
}

sub page {
    my ($file) = @_;
    open(my $fh, '<:raw', "$pages/$file") or return "missing page: $file\n";
    local $/;
    my $text = <$fh>;
    close $fh;
    my $token = join '', map { ('a' .. 'z', 0 .. 9)[int rand 36] } 1 .. 10;
    $text =~ s/\{\{TOKEN\}\}/$token/g;
    return $text;
}

sub reply {
    my ($c, $status, $type, $body) = @_;
    my $data = "HTTP/1.1 $status\r\nContent-Type: $type\r\nContent-Length: " . length($body)
        . "\r\nCache-Control: no-store\r\nX-Josour-Spike: listener\r\nConnection: close\r\n\r\n" . $body;
    my $offset = 0;
    while ($offset < length $data) {
        my $n = syswrite($c, $data, length($data) - $offset, $offset);
        last unless $n;
        $offset += $n;
    }
    close $c;
}
